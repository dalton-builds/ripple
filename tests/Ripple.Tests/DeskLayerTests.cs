using Ripple;
using Ripple.Presets;
using Ripple.Render;
using Xunit;
using Xunit.Abstractions;

namespace Ripple.Tests;

/// <summary>
/// The Phase 4 control logic: the governor's loop, §11's idle states, §12's non-overridable
/// safety floor, the budget gate's own arithmetic, and config tolerance.
///
/// All of it is deliberately GL-free. These are the parts where a mistake is a slow
/// behavioural fault rather than a visible one — a governor that oscillates, an idle state
/// that never comes back, a preset that quietly relaxes the photosensitivity limiter — and
/// none of them need a context to be pinned down.
/// </summary>
public sealed class DeskLayerTests(ITestOutputHelper log)
{
    // ------------------------------------------------------------- governor

    /// <summary>The dial ladder is §11's, and tier 0 must be the exact identity.</summary>
    [Fact]
    public void Governor_tier_zero_changes_nothing()
    {
        var g = new Governor();
        g.SetLevel(0);

        var d = new DrawState
        {
            Octaves = 5, Iterations = 128, PCount = 64000, BloomTaps = 3,
            BloomGain = 0.8, Fractal = "apollonian",
        };
        g.Apply(d);

        Assert.Equal(5, d.Octaves);
        Assert.Equal(128, d.Iterations);
        Assert.Equal(64000, d.PCount);
        Assert.Equal(3, d.BloomTaps);
        // The expensive family survives at tier 0; this is what keeps a parity run pinned to
        // tier 0 measuring the shaders the preset actually asked for.
        Assert.Equal("apollonian", d.Fractal);
    }

    /// <summary>
    /// An iteration count below the tier-0 floor must not be raised. A Math.Max applied
    /// unconditionally would silently rewrite an authored look, and it would surface as a
    /// parity failure that looks like a shader bug.
    /// </summary>
    [Fact]
    public void Governor_tier_zero_does_not_raise_a_low_iteration_count()
    {
        var g = new Governor();
        g.SetLevel(0);
        var d = new DrawState { Iterations = 8 };
        g.Apply(d);
        Assert.Equal(8, d.Iterations);
    }

    [Fact]
    public void Governor_pulls_the_dials_in_the_planned_order()
    {
        var g = new Governor();
        double prevScale = double.MaxValue, prevParticles = double.MaxValue;

        for (int level = 0; level < Governor.Tiers.Length; level++)
        {
            g.SetLevel(level);
            var t = g.Current;
            var d = new DrawState
            {
                Octaves = 6, Iterations = 128, PCount = 64000, BloomTaps = 3,
                Fractal = "mandel_zoom",
            };
            g.Apply(d);

            log.WriteLine($"tier {level} ({t.Label,-13}) scale {t.Scale:0.00} "
                + $"particles {d.PCount,8:0} iter {d.Iterations,6:0} taps {d.BloomTaps} "
                + $"fractal {d.Fractal}");

            // Monotone in the direction that matters: every rung must be cheaper than the one
            // above it, or the ladder has a step that buys nothing.
            Assert.True(t.Scale <= prevScale, $"tier {level} scale rose");
            Assert.True(d.PCount <= prevParticles, $"tier {level} particle count rose");
            prevScale = t.Scale;
            prevParticles = d.PCount;
        }

        // §11's last dial before the floor.
        g.SetLevel(Governor.Tiers.Length - 1);
        var last = new DrawState { Fractal = "mandel_zoom", Iterations = 128 };
        g.Apply(last);
        Assert.Equal("warp_fbm", last.Fractal);
    }

    /// <summary>
    /// 30 CONSECUTIVE frames over budget, and the run has to be unbroken: a single good frame
    /// resets it. Without that, one hitch from a shader recompile drops quality for everyone.
    /// </summary>
    [Fact]
    public void Governor_steps_down_only_after_thirty_consecutive_bad_frames()
    {
        var g = MakeAutoGovernor();
        double over = g.BudgetMs * 2;

        // 29 bad frames, then a good one, 29 more: 58 bad frames total, no step.
        for (int i = 0; i < 29; i++) g.Tick(1.0 / 60, over, true);
        Assert.Equal(0, g.Level);
        g.Tick(1.0 / 60, g.BudgetMs * 0.9, true);      // in the dead band — resets the run
        for (int i = 0; i < 29; i++) g.Tick(1.0 / 60, over, true);
        Assert.Equal(0, g.Level);
        log.WriteLine("58 bad frames broken by one good one: still tier 0");

        // Now an unbroken run of 30.
        for (int i = 0; i < 30; i++) g.Tick(1.0 / 60, over, true);
        Assert.Equal(1, g.Level);
        log.WriteLine($"30 consecutive bad frames: tier {g.Level}");
    }

    /// <summary>§11's 5 s dwell. Two steps inside five seconds is the oscillation it forbids.</summary>
    [Fact]
    public void Governor_holds_a_tier_for_the_dwell_time()
    {
        var g = MakeAutoGovernor();
        double over = g.BudgetMs * 3;

        for (int i = 0; i < 30; i++) g.Tick(1.0 / 60, over, true);
        Assert.Equal(1, g.Level);

        // Hammer it with bad frames but advance almost no time: the dwell must hold.
        for (int i = 0; i < 600; i++) g.Tick(0.0001, over, true);
        Assert.Equal(1, g.Level);
        log.WriteLine($"600 bad frames inside the dwell window: still tier {g.Level}");

        // Let the dwell expire, then 30 more.
        g.Tick(6.0, over, true);
        for (int i = 0; i < 30; i++) g.Tick(1.0 / 60, over, true);
        Assert.Equal(2, g.Level);
    }

    [Fact]
    public void Governor_steps_back_up_after_three_hundred_good_frames()
    {
        var g = MakeAutoGovernor();
        for (int i = 0; i < 30; i++) g.Tick(1.0 / 60, g.BudgetMs * 3, true);
        Assert.Equal(1, g.Level);

        // Clear the dwell with a DEAD-BAND frame, not a good one: a good frame would also be
        // the first of the 300-frame run and make the count below off by one.
        g.Tick(6.0, g.BudgetMs * 0.8, true);
        double good = g.BudgetMs * 0.4;                // comfortably under 55%

        for (int i = 0; i < 299; i++) g.Tick(1.0 / 60, good, true);
        Assert.Equal(1, g.Level);
        for (int i = 0; i < 2; i++) g.Tick(1.0 / 60, good, true);
        Assert.Equal(0, g.Level);
        log.WriteLine($"300 good frames: back to tier {g.Level}, {g.StepUps} step-up(s)");
    }

    /// <summary>
    /// A frame inside the dead band — under budget but not under 55% — must not count toward
    /// stepping up. That gap is the anti-oscillation margin: stepping up on a frame that only
    /// just fits guarantees the next tier is over budget.
    /// </summary>
    [Fact]
    public void Governor_does_not_step_up_from_inside_the_dead_band()
    {
        var g = MakeAutoGovernor();
        for (int i = 0; i < 30; i++) g.Tick(1.0 / 60, g.BudgetMs * 3, true);
        g.Tick(6.0, g.BudgetMs * 0.8, true);

        for (int i = 0; i < 1000; i++) g.Tick(1.0 / 60, g.BudgetMs * 0.8, true);
        Assert.Equal(1, g.Level);
        log.WriteLine("1000 dead-band frames: no step up, as intended");
    }

    [Fact]
    public void Governor_budget_is_seventy_percent_of_the_frame_period()
    {
        var g = new Governor();
        g.SetTargetFps(60);
        Assert.Equal(1000.0 / 60 * 0.7, g.BudgetMs, 6);
        g.SetTargetFps(30);
        Assert.Equal(1000.0 / 30 * 0.7, g.BudgetMs, 6);
    }

    [Fact]
    public void Governor_does_nothing_when_auto_is_off()
    {
        var g = new Governor { Auto = false };
        g.SetTargetFps(60);
        for (int i = 0; i < 5000; i++) g.Tick(1.0 / 60, g.BudgetMs * 10, true);
        Assert.Equal(0, g.Level);
    }

    private static Governor MakeAutoGovernor()
    {
        var g = new Governor { Auto = true };
        g.SetTargetFps(60);
        g.SetLevel(0);
        // SetLevel zeroes the dwell, so advance past it before the first decision.
        g.Tick(6.0, g.BudgetMs * 0.9, true);
        return g;
    }

    // ----------------------------------------------------------- idle states

    [Fact]
    public void Idle_dims_to_thirty_percent_and_drops_to_ten_fps_when_audio_stops()
    {
        var idle = new IdleController();
        const double dt = 1.0 / 60;

        // Live: every frame renders at full brightness.
        for (int i = 0; i < 60; i++) Assert.True(idle.ShouldRender(dt, false, true));
        Assert.Equal(1.0, idle.Dim, 3);

        // Quiet: ramp down, then rate-limit.
        for (int i = 0; i < 600; i++) idle.ShouldRender(dt, true, true);
        Assert.Equal(IdleController.State.Quiet, idle.Current);
        Assert.Equal(0.30, idle.Dim, 2);
        Assert.True(idle.FreezeFeedback, "feedback decay should be frozen while quiet");

        // Count renders over a simulated second: should be ~10, not 60.
        int rendered = 0;
        for (int i = 0; i < 60; i++) if (idle.ShouldRender(dt, true, true)) rendered++;
        log.WriteLine($"quiet: {rendered} renders per simulated second, dim {idle.Dim:0.00}");
        Assert.InRange(rendered, 8, 12);
    }

    [Fact]
    public void Idle_stops_rendering_entirely_when_hidden()
    {
        var idle = new IdleController();
        for (int i = 0; i < 100; i++)
            Assert.False(idle.ShouldRender(1.0 / 60, false, visible: false));
        Assert.Equal(IdleController.State.Hidden, idle.Current);
        // Nothing to sleep on: the caller blocks on the event loop instead of polling.
        Assert.Equal(0, idle.SleepSeconds);
    }

    /// <summary>
    /// §11's 500 ms ramp. Coming back has to be a fade rather than a cut, because a jump from
    /// 30% to full IS a luminance step and §12's limiter would clamp it — so not ramping shows
    /// up as a lurch followed by a dip.
    /// </summary>
    [Fact]
    public void Idle_ramps_back_over_about_five_hundred_milliseconds()
    {
        var idle = new IdleController();
        const double dt = 1.0 / 60;
        for (int i = 0; i < 600; i++) idle.ShouldRender(dt, true, true);
        Assert.Equal(0.30, idle.Dim, 2);

        double elapsed = 0;
        while (idle.Dim < 0.99 && elapsed < 3.0)
        {
            idle.ShouldRender(dt, false, true);
            elapsed += dt;
        }

        log.WriteLine($"ramped 0.30 -> {idle.Dim:0.000} in {elapsed * 1000:0} ms");
        Assert.True(idle.Dim > 0.99, $"never came back (dim {idle.Dim:0.00})");
        // Generous bounds: the assertion is "half a second-ish, not instant and not glacial".
        Assert.InRange(elapsed, 0.2, 1.2);
    }

    [Fact]
    public void Idle_never_dims_below_the_specified_floor()
    {
        var idle = new IdleController();
        for (int i = 0; i < 10_000; i++) idle.ShouldRender(1.0 / 60, true, true);
        Assert.True(idle.Dim >= 0.30 - 1e-9, $"dimmed to {idle.Dim}, past the 0.30 floor");
    }

    // --------------------------------------------------------------- safety

    /// <summary>
    /// §12 is UNCONDITIONAL. A preset may be stricter than the floor and may never be looser,
    /// and the clamp has to live somewhere a preset cannot route around — which is
    /// DrawState.Reset, on every frame, rather than in a validator at load time.
    /// </summary>
    [Theory]
    [InlineData(0.5, 1.0)]        // a preset asking for the sky
    [InlineData(0.12, 0.92)]      // exactly at the ceiling
    [InlineData(0.02, 0.40)]      // stricter than the floor: must be honoured as-is
    public void Preset_cannot_relax_the_safety_limiter(double askDelta, double askBright)
    {
        var p = new PresetValues { MaxLuminanceDelta = askDelta, MaxBrightness = askBright };
        var d = new DrawState();
        d.Reset(p);

        log.WriteLine($"asked delta {askDelta:0.00} bright {askBright:0.00} "
            + $"-> got delta {d.MaxLumDelta:0.00} bright {d.MaxBright:0.00}");

        Assert.True(d.MaxLumDelta <= 0.12 + 1e-9, "luminance delta ceiling breached");
        Assert.True(d.MaxBright <= 0.92 + 1e-9, "brightness ceiling breached — never full white");
        Assert.Equal(Math.Min(askDelta, 0.12), d.MaxLumDelta, 6);
        Assert.Equal(Math.Min(askBright, 0.92), d.MaxBright, 6);
    }

    [Fact]
    public void Default_safety_values_are_the_plan_numbers()
    {
        var d = new DrawState();
        // §12: maxLuminanceDelta 0.08, brightness ceiling 0.85.
        Assert.Equal(0.08, d.MaxLumDelta, 6);
        Assert.Equal(0.85, d.MaxBright, 6);
    }

    // ---------------------------------------------------------- budget gate

    [Fact]
    public void Budget_gate_ignores_the_warmup_then_grades_the_window()
    {
        var gate = new BudgetGate();
        for (int i = 0; i < BudgetGate.WarmupFrames - 1; i++) gate.Sample(500);
        Assert.False(gate.Open);
        Assert.Equal(0, gate.Samples);

        for (int i = 0; i < 1200; i++) gate.Sample(4.0);
        Assert.True(gate.Open);

        var r = gate.Evaluate();
        log.WriteLine($"p50 {r.P50Ms:0.00} p99 {r.P99Ms:0.00} gen0 {r.Gen0} pass {r.Pass}");
        // The 500 ms warm-up frames must not be in the percentile.
        Assert.Equal(4.0, r.P99Ms, 3);
    }

    [Fact]
    public void Budget_gate_fails_a_slow_p99()
    {
        var gate = new BudgetGate(workingSetBudgetBytes: long.MaxValue);
        for (int i = 0; i < BudgetGate.WarmupFrames; i++) gate.Sample(1.0);
        // 98% fast, 2% very slow: the mean would pass and the p99 must not.
        for (int i = 0; i < 1000; i++) gate.Sample(i % 50 == 0 ? 40.0 : 2.0);

        var r = gate.Evaluate();
        log.WriteLine($"p99 {r.P99Ms:0.00} ms -> pass {r.Pass}");
        Assert.False(r.Pass);
        Assert.Contains(r.Failures, f => f.Contains("p99"));
    }

    // --------------------------------------------------------------- config

    [Fact]
    public void Config_survives_a_partial_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ripple-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "window": { "alwaysOnTop": true }, "render": { "targetFps": 30 } }
            """);
        try
        {
            var cfg = RippleConfig.LoadOrCreate(path);
            Assert.True(cfg.AlwaysOnTop);
            Assert.Equal(30, cfg.TargetFps);
            // Everything unmentioned keeps its default rather than becoming zero.
            Assert.Equal(960, cfg.Width);
            Assert.Equal(540, cfg.Height);
            Assert.True(cfg.Audio);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// A malformed config must not lose the running values. This is the mid-edit case: an
    /// editor saves, the watcher fires, and the file has a trailing comma for a few
    /// milliseconds.
    /// </summary>
    [Fact]
    public void Config_reload_keeps_previous_values_on_a_parse_error()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ripple-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "render": { "targetFps": 45 } }""");
        try
        {
            var cfg = RippleConfig.LoadOrCreate(path);
            Assert.Equal(45, cfg.TargetFps);

            File.WriteAllText(path, "{ this is not json");
            Assert.False(cfg.Reload(path));
            Assert.Equal(45, cfg.TargetFps);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Master intensity is a SAFETY control, so the config cannot use it to push past 1.0 any
    /// more than a preset can. It only ever takes brightness away.
    /// </summary>
    [Fact]
    public void Config_cannot_raise_master_intensity_above_one()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ripple-cfg-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "safety": { "masterIntensity": 8.0 } }""");
        try
        {
            var cfg = RippleConfig.LoadOrCreate(path);
            Assert.Equal(1.0, cfg.MasterIntensity, 6);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Config_first_run_writes_a_file_that_reads_back_identically()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ripple-{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "config.json");
        try
        {
            var a = RippleConfig.LoadOrCreate(path);
            Assert.True(File.Exists(path), "first run should leave a discoverable config");

            var b = RippleConfig.LoadOrCreate(path);
            Assert.Equal(a.Width, b.Width);
            Assert.Equal(a.TargetFps, b.TargetFps);
            Assert.Equal(a.GovernorAuto, b.GovernorAuto);
            Assert.Equal(a.Audio, b.Audio);
            Assert.Equal(a.Corner, b.Corner);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>A missing file and an unwritable path must both still yield a usable config.</summary>
    [Fact]
    public void Config_never_throws()
    {
        var cfg = RippleConfig.LoadOrCreate(
            Path.Combine("Z:\\", "definitely-not-here", "config.json"));
        Assert.Equal(960, cfg.Width);
        Assert.True(cfg.Audio);
    }
}
