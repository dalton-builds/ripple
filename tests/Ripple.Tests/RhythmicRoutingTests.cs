using System.Text.Json.Nodes;
using Ripple.Presets;
using Ripple.Render;
using Xunit;
using Xunit.Abstractions;

namespace Ripple.Tests;

/// <summary>
/// The motion-sync layer: pulse shaping of the phase fields, beat-relative visual envelopes,
/// and the constraint that beat response goes through geometry rather than luminance.
///
/// The last one is a test rather than a comment because it is the easiest thing to undo by
/// accident. A brightness pulse on the beat looks correct in isolation and is actively
/// self-defeating in place: §12's limiter caps mean-luminance delta per frame, so the pulse
/// gets clamped and the limiter dims the whole image in response to the beat.
/// </summary>
public sealed class RhythmicRoutingTests(ITestOutputHelper log)
{
    private static PresetValues Preset(params (string Source, string Target)[] mapping)
    {
        var p = new PresetValues { Zoom = 1.0035, WarpAmount = 0.38, BeatMorph = 0.55, Cells = 4.6 };
        var list = new List<(int, MapTarget)>();
        foreach (var (src, tgt) in mapping)
        {
            int idx = Array.IndexOf(FF.Names, src);
            Assert.True(idx >= 0, $"no such feature field '{src}'");
            var node = JsonNode.Parse($"{{\"mapping\":{{\"{src}\":\"{tgt}\"}}}}")!.AsObject();
            var parsed = PresetValues.From(node);
            Assert.Single(parsed.Mapping);
            list.Add(parsed.Mapping[0]);
        }
        p.Mapping = [.. list];
        return p;
    }

    private static float[] Frame(double beatPhase, double barPhase, double conf,
                                 double onset = 0, double flux = 0, double bpm = 120)
    {
        var f = new float[FeatureFrame.FloatCount];
        f[FF.BeatPhase] = (float)beatPhase;
        f[FF.BarPhase] = (float)barPhase;
        f[FF.BpmConf] = (float)conf;
        f[FF.Onset] = (float)onset;
        f[FF.Flux] = (float)flux;
        f[FF.Bpm] = (float)bpm;
        return f;
    }

    // ------------------------------------------------------- pulse shaping

    /// <summary>
    /// beatPhase is a RAMP: 0 on the beat, climbing to 1 just before the next. Routed raw it
    /// makes a parameter drift up and snap back — a sawtooth, which reads as a glitch on the
    /// beat rather than a pulse on it. The shaped form has to be largest ON the beat.
    /// </summary>
    [Fact]
    public void Beat_phase_is_shaped_into_a_pulse_that_peaks_on_the_beat()
    {
        var p = Preset(("beatPhase", "feedbackZoomPulse"));

        var onBeat = new DrawState(); onBeat.Reset(p);
        onBeat.ApplyMapping(p, Frame(beatPhase: 0.0, barPhase: 0, conf: 1));

        var midBeat = new DrawState(); midBeat.Reset(p);
        midBeat.ApplyMapping(p, Frame(beatPhase: 0.5, barPhase: 0, conf: 1));

        var justBefore = new DrawState(); justBefore.Reset(p);
        justBefore.ApplyMapping(p, Frame(beatPhase: 0.99, barPhase: 0, conf: 1));

        log.WriteLine($"zoom on the beat {onBeat.Zoom:F5}, mid-beat {midBeat.Zoom:F5}, "
            + $"just before the next {justBefore.Zoom:F5}");

        Assert.True(onBeat.Zoom > midBeat.Zoom, "the pulse is not largest on the beat");
        Assert.True(midBeat.Zoom > justBefore.Zoom, "the pulse is not decaying across the beat");
        // And it has to reach the target's full gain on the beat, or the shaping is eating it.
        Assert.Equal(p.Zoom + 0.012, onBeat.Zoom, 5);
    }

    /// <summary>
    /// A grid the DSP does not trust must drive nothing. Below §7.3's floor the DSP is
    /// synthesising beatPhase from the onset envelope, and a full-strength pulse on a guessed
    /// grid is worse than no pulse at all.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(0.0)]
    public void Pulse_scales_with_bpm_confidence(double conf)
    {
        var p = Preset(("beatPhase", "feedbackZoomPulse"));
        var d = new DrawState();
        d.Reset(p);
        d.ApplyMapping(p, Frame(beatPhase: 0.0, barPhase: 0, conf: conf));

        double applied = d.Zoom - p.Zoom;
        log.WriteLine($"conf {conf:F1} -> zoom pulse {applied:F5} (full gain 0.012)");
        Assert.Equal(0.012 * conf, applied, 5);
    }

    /// <summary>
    /// The two routes that measured a real effect. Kept as a guard because the arithmetic in
    /// ApplyMapping is a shared contract with the harness, and a gain edited on one side only
    /// is the failure this catches.
    /// </summary>
    [Fact]
    public void Onset_and_flux_routes_move_their_targets()
    {
        var p = Preset(("onset", "warpAmount"), ("flux", "fractalMorph"));

        var quiet = new DrawState(); quiet.Reset(p);
        quiet.ApplyMapping(p, Frame(0.5, 0.5, 1, onset: 0.0, flux: 0.0));

        var hit = new DrawState(); hit.Reset(p);
        hit.ApplyMapping(p, Frame(0.5, 0.5, 1, onset: 1.0, flux: 1.0));

        log.WriteLine($"warpAmount {quiet.WarpAmount:F3} -> {hit.WarpAmount:F3}; "
            + $"beatMorph {quiet.BeatMorph:F3} -> {hit.BeatMorph:F3}; "
            + $"cells {quiet.Cells:F3} -> {hit.Cells:F3}");

        Assert.True(hit.WarpAmount > quiet.WarpAmount * 3, "onset barely moved the churn");
        Assert.True(hit.BeatMorph > quiet.BeatMorph * 2, "flux barely moved the morph");
        Assert.True(hit.Cells > quiet.Cells, "flux did not move the cell density");
    }

    // ------------------------------------------- beat-relative envelopes

    /// <summary>
    /// A fixed 155 ms bolt tail resolves at an arbitrary point in the bar — long gone before
    /// the next beat at 80 BPM, still visible when it lands at 170. Scaling by 60/BPM makes a
    /// pulse resolve in the same musical time at any tempo.
    /// </summary>
    [Fact]
    public void Motif_envelopes_scale_with_tempo()
    {
        double BoltAfter(double bpm, double conf, double seconds)
        {
            var h = new HostState();
            var d = new DrawState();
            d.Reset(new PresetValues { BoltThreshold = 0.0 });
            var f = Frame(0.5, 0.5, conf, onset: 1.0, bpm: bpm);
            f[FF.Percussivity] = 1.0f;

            const double dt = 1.0 / 60;
            h.AdvanceMotifs(dt, d, f);        // fires
            f[FF.Onset] = 0;                  // then decays
            for (double t = 0; t < seconds; t += dt) h.AdvanceMotifs(dt, d, f);
            return h.Bolt;
        }

        // Half a second in: a fast tempo should have resolved further than a slow one.
        double fast = BoltAfter(170, 1.0, 0.5);
        double slow = BoltAfter(70, 1.0, 0.5);
        log.WriteLine($"bolt after 0.5 s — 170 BPM {fast:F5}, 70 BPM {slow:F5}");
        Assert.True(fast < slow, "a faster tempo did not shorten the envelope");

        // And with no confidence, tempo must not matter at all: both fall back to the
        // authored constant.
        double fastUntrusted = BoltAfter(170, 0.0, 0.5);
        double slowUntrusted = BoltAfter(70, 0.0, 0.5);
        log.WriteLine($"untrusted grid — 170 BPM {fastUntrusted:F5}, 70 BPM {slowUntrusted:F5}");
        Assert.Equal(fastUntrusted, slowUntrusted, 6);
    }

    [Fact]
    public void Beat_relative_scaling_is_clamped()
    {
        // An absurd BPM must not stretch or crush the envelope without limit: the scale is
        // clamped to [0.5, 2.0] so a bad tempo reading cannot turn a 155 ms tail into
        // something measured in seconds.
        var h = new HostState();
        var d = new DrawState();
        d.Reset(new PresetValues { BoltThreshold = 0.0 });

        foreach (double bpm in (double[])[31, 60, 104, 180])
        {
            var f = Frame(0.5, 0.5, 1.0, onset: 1.0, bpm: bpm);
            f[FF.Percussivity] = 1.0f;
            h.Reset();
            h.AdvanceMotifs(1.0 / 60, d, f);
            f[FF.Onset] = 0;
            for (int i = 0; i < 30; i++) h.AdvanceMotifs(1.0 / 60, d, f);
            log.WriteLine($"{bpm,4:F0} BPM -> bolt {h.Bolt:F5} after 0.5 s");
            Assert.InRange(h.Bolt, 0.0, 1.0);
        }
    }

    // ------------------------------------------------- the §12 constraint

    /// <summary>
    /// BEAT RESPONSE GOES THROUGH GEOMETRY, NOT BRIGHTNESS.
    ///
    /// warp_fbm used to carry `col *= ... 0.55 * beatPulse(4.0) * F_BPM_CONF + 0.65 *
    /// F_ONSET * F_PERCUSSIVITY`, a rhythm-driven brightness swing of up to 5.8x. That is
    /// self-defeating: §12's limiter caps mean-luminance delta per frame, so the pulse is
    /// clamped and SafeScale drops, dimming the whole image in response to the beat. The
    /// terms moved into the field scale and the warp amount, which are not rate-limited.
    ///
    /// Asserted against the shader source, because this is a property of where the terms
    /// live and there is no way to observe it from the host side.
    /// </summary>
    [Fact]
    public void Beat_response_is_not_wired_into_brightness()
    {
        string root = FindRepoRoot();
        string src = File.ReadAllText(Path.Combine(root, "shaders", "fractal", "warp_fbm.frag"));

        // The exposure line: everything multiplied into `col` at the end.
        var exposure = src.Split('\n')
            .Where(l => l.TrimStart().StartsWith("col *="))
            .ToList();
        Assert.NotEmpty(exposure);

        foreach (var line in exposure)
        {
            log.WriteLine($"exposure line: {line.Trim()}");
            Assert.DoesNotContain("beatPulse", line);
            Assert.DoesNotContain("barPulse", line);
            Assert.DoesNotContain("F_BEAT_PHASE", line);
            Assert.DoesNotContain("F_ONSET", line);
        }

        // And it has to still be there, in geometry — a test that only forbids is a test that
        // passes when the feature is deleted.
        Assert.Contains("beatPulse", src);
        Assert.Contains("F_ONSET", src);
    }

    /// <summary>Every era preset carries the rhythmic routes, or the wiring was half-done.</summary>
    [Fact]
    public void Every_era_preset_is_wired_for_rhythm()
    {
        string eras = Path.Combine(FindRepoRoot(), "presets", "eras");
        var files = Directory.GetFiles(eras, "*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var node = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var v = PresetValues.From(node);
            var sources = v.Mapping.Select(m => FF.Names[m.Field]).ToHashSet();

            log.WriteLine($"{Path.GetFileName(file),-20} {string.Join(", ", sources.Order())}");

            Assert.Contains("beatPhase", sources);
            Assert.Contains("onset", sources);
            Assert.Contains("flux", sources);
            // barPhase is deliberately absent — measured r = -0.028, swamped by keyHue's
            // unbounded accumulation into palRot. See tools/rhythm-depth.mjs.
            Assert.DoesNotContain("barPhase", sources);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; up < 8 && dir is not null; up++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "shaders"))
             && Directory.Exists(Path.Combine(dir.FullName, "presets")))
                return dir.FullName;
        throw new DirectoryNotFoundException("could not locate the repo root from "
            + AppContext.BaseDirectory);
    }
}
