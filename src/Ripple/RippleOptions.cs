namespace Ripple;

/// <summary>Command-line configuration. Phase 5 replaces most of this with a config file.</summary>
public sealed class RippleOptions
{
    public string ShaderRoot = "shaders";
    public string PresetRoot = "presets";
    public string DataRoot = "data";

    public int Width = 960;
    public int Height = 540;
    public double RenderScale = 0.75;
    public bool VSync = true;

    /// <summary>Preset id or library index. Empty means the first in the library.</summary>
    public string Preset = "";
    public string Song = "";

    /// <summary>
    /// `--meta-script "Title|Artist|Album;..."` — drive the metadata layer from the command
    /// line instead of from SMTC, so §9.4's tiers can be exercised on demand. Diagnostic only;
    /// see <see cref="Meta.ScriptedMetadataSource"/> for why it had to exist.
    /// </summary>
    public string MetaScript = "";

    /// <summary>
    /// `--windowed` — force an ordinary titled, resizable window for this run.
    ///
    /// The config defaults `borderless` to true because that is the right look on a desk, and
    /// there was no way to override it for one run short of editing config.json. That makes
    /// testing awkward: a borderless window cannot be dragged off the thing you are comparing
    /// it against. This overrides the config without writing to it.
    /// </summary>
    public bool Windowed;

    /// <summary>
    /// Drive the visuals from real desktop audio (WASAPI loopback) rather than from the
    /// synthetic driver. The synthetic driver stays the parity reference and the fallback,
    /// so this is opt-in for now; Phase 5 makes it the default.
    /// </summary>
    public bool Audio;

    /// <summary>Record real-audio Feature Frames to this timeline file, then exit.</summary>
    public string? RecordFeatures;
    public double RecordSeconds = 60;

    /// <summary>
    /// What this capture is OF — "tight-backbeat, Sugar Magnolia 1972". Stored in the
    /// timeline so a fixture is self-describing: the filename says which capture it is, the
    /// label says what it was supposed to demonstrate, and six months later only one of
    /// those is still obvious.
    /// </summary>
    public string RecordLabel = "";

    /// <summary>Timeline to replay instead of the synthetic driver.</summary>
    public string? Timeline;

    /// <summary>Exact frame stepping — one timeline row per rendered frame.</summary>
    public bool Step;

    // --- batch dump ---------------------------------------------------------
    public bool Dump;
    public string DumpOut = "captures/native";
    public int Stride = 10;
    /// <summary>Dump every preset in the library rather than just the selected one.</summary>
    public bool DumpAll;

    public bool ShaderHotReload = true;

    /// <summary>
    /// Mode A of the parity gate: clear the ping-pong feedback chain before
    /// every step, so each frame is computed fresh from its Feature Frame with
    /// no accumulation.
    ///
    /// This exists because the renderer is a feedback renderer. Two GL
    /// implementations that agree to the last representable bit on frame 1 will
    /// still diverge visibly by frame 600, because each frame warps the
    /// previous one and the difference compounds. That divergence is the
    /// feedback loop doing its job, not a broken contract — so the strict gate
    /// takes the accumulation out of the path and tests the shader math alone.
    /// </summary>
    public bool NoFeedback;

    /// <summary>
    /// Drop the film grain. Part of gate mode A, because the grain is a fract-chain
    /// hash whose value is chaotic in the last bit of its input — see the measurement
    /// recorded against "grain" in tests/parity-config.json.
    /// </summary>
    public bool NoGrain;

    /// <summary>
    /// Diagnostic: drop the particle layer. Particles are GL_POINTS, and
    /// sub-pixel point rasterization is not specified tightly enough for two
    /// implementations to agree — ANGLE expands sized points to quads on D3D11,
    /// desktop GL rasterizes them natively. Use this to tell "the particle
    /// layer landed a pixel over" apart from "the shaders disagree".
    /// </summary>
    public bool NoParticles;

    /// <summary>
    /// Write a per-frame scalar trace of the draw state next to the dump. The
    /// harness produces the same rows through __ripple.trace(), so
    /// tools/compare-trace.mjs can diff them and say WHICH parameter diverged
    /// rather than leaving an SSIM number to be interpreted.
    /// </summary>
    public bool Trace;

    /// <summary>
    /// §11's footprint budget as a gate: render the timeline, assert p99 / private bytes /
    /// zero Gen0, exit nonzero on failure. See <see cref="Render.BudgetGate"/>.
    /// </summary>
    public bool BudgetTest;
    public double BudgetSeconds = 60;

    /// <summary>
    /// Override the private-bytes ceiling, in MB. 0 keeps the committed 128. Exists so the
    /// budget can be rebased from the command line rather than by editing a constant.
    /// </summary>
    public long BudgetPrivateMb;

    /// <summary>
    /// Long-run soak: render for this many hours, reporting working set periodically, then
    /// exit. 0 means no soak. The point is drift, not throughput — a leak of 1 MB an hour is
    /// invisible in a 60 s test and fatal in a work day.
    /// </summary>
    public double SoakHours;

    /// <summary>Gate mode from tests/parity-config.json, or empty for none.</summary>
    public string ParityMode = "";
    public string ParityConfig = ParityProfile.DefaultPath;

    // Set when the value came from the command line, so a parity mode never overrides
    // something the caller asked for explicitly.
    public bool SizeSpecified, ScaleSpecified, StrideSpecified;

    public static RippleOptions Parse(string[] args)
    {
        var o = new RippleOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next(string name) => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"{name} needs a value");

            switch (a)
            {
                case "--shaders": o.ShaderRoot = Next(a); break;
                case "--presets": o.PresetRoot = Next(a); break;
                case "--data": o.DataRoot = Next(a); break;
                case "--preset": o.Preset = Next(a); break;
                case "--song": o.Song = Next(a); break;
                case "--meta-script": o.MetaScript = Next(a); break;
                case "--windowed": o.Windowed = true; break;
                case "--timeline": o.Timeline = Next(a); break;
                case "--step": o.Step = true; break;
                case "--no-vsync": o.VSync = false; break;
                case "--no-hot-reload": o.ShaderHotReload = false; break;
                case "--audio": o.Audio = true; break;
                case "--record-features": o.RecordFeatures = Next(a); o.Audio = true; break;
                case "--record-seconds": o.RecordSeconds = double.Parse(Next(a), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--record-label": o.RecordLabel = Next(a); break;
                case "--no-feedback": o.NoFeedback = true; break;
                case "--no-grain": o.NoGrain = true; break;
                case "--parity-mode": o.ParityMode = Next(a); break;
                case "--parity-config": o.ParityConfig = Next(a); break;
                case "--no-particles": o.NoParticles = true; break;
                case "--trace": o.Trace = true; break;
                case "--budget-test":
                    o.BudgetTest = true;
                    // The timeline drives it, but the gate owns the defaults: vsync would
                    // cap every frame at the refresh interval and the p99 would measure the
                    // display rather than the renderer.
                    o.VSync = false;
                    break;
                case "--budget-private":
                    o.BudgetPrivateMb = long.Parse(Next(a),
                        System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--budget-seconds":
                    o.BudgetSeconds = double.Parse(Next(a),
                        System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--soak":
                    o.SoakHours = double.Parse(Next(a),
                        System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--scale": o.RenderScale = double.Parse(Next(a), System.Globalization.CultureInfo.InvariantCulture); o.ScaleSpecified = true; break;
                case "--stride": o.Stride = int.Parse(Next(a), System.Globalization.CultureInfo.InvariantCulture); o.StrideSpecified = true; break;
                case "--dump": o.Dump = true; o.Step = true; break;
                case "--out": o.DumpOut = Next(a); break;
                case "--all": o.DumpAll = true; break;
                case "--size":
                {
                    var parts = Next(a).Split('x', 'X');
                    if (parts.Length != 2) throw new ArgumentException("--size wants WxH, e.g. 960x540");
                    o.Width = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                    o.Height = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    o.SizeSpecified = true;
                    break;
                }
                case "-h":
                case "--help":
                    throw new HelpRequested();
                default:
                    throw new ArgumentException($"unknown argument: {a}");
            }
        }

        if (o.Audio && !string.IsNullOrEmpty(o.Timeline))
            throw new ArgumentException("--audio and --timeline are alternatives: one drives "
                + "the visuals from live capture, the other replays a recording");
        if (o.RecordSeconds <= 0)
            throw new ArgumentException("--record-seconds must be positive");

        if (o.Dump && string.IsNullOrEmpty(o.Timeline))
            throw new ArgumentException("--dump needs --timeline: the parity gate replays a recorded timeline");
        if (o.Step && string.IsNullOrEmpty(o.Timeline))
            throw new ArgumentException("--step needs --timeline");

        if (o.BudgetTest && string.IsNullOrEmpty(o.Timeline))
            o.Timeline = "tests/fixtures/timeline-budget-60s.json";
        if (o.BudgetSeconds <= 0) throw new ArgumentException("--budget-seconds must be positive");
        if (o.SoakHours < 0) throw new ArgumentException("--soak must not be negative");
        if (o.BudgetTest && o.SoakHours > 0)
            throw new ArgumentException("--budget-test and --soak are separate runs: one grades "
                + "a 60 s window, the other watches for drift over hours");

        return o;
    }

    public sealed class HelpRequested : Exception;

    public const string Usage = """
        Ripple — Phase 1 native shell

          Ripple [--audio] [--preset <id|index>] [--song <title>] [--size 960x540]
                 [--scale 0.75] [--timeline <file.json> [--step]] [--no-vsync]
                 [--no-hot-reload] [--windowed]

          Ripple --record-features <out.json> [--record-seconds 60]
                 [--record-label "what this capture is of"]

          Ripple --dump --timeline <file.json> --out <dir> [--all]
                 [--parity-mode a|b] [--preset <id|index>] [--stride N]
                 [--size 960x540] [--scale 0.75] [--trace]

          Ripple --budget-test [--budget-seconds 60] [--budget-private MB]
          Ripple --soak <hours>

          Ripple --audio --meta-script "Title|Artist|Album;Title|Artist|Album"

        With no arguments at all, Ripple reads %APPDATA%\Ripple\config.json —
        creating it with defaults on first run — and drives itself from desktop
        audio. Nothing below is needed to just run it.

        --meta-script drives the metadata layer from these tracks instead of
        from SMTC, advancing every 12 s, so §9.4's degradation tiers can be
        exercised without waiting for the right thing to come up on shuffle.
        Artist defaults to "Grateful Dead". It is a diagnostic: the HUD and the
        log both say `scripted`, because a rehearsed caption must never be
        mistaken for an observed one.

        --budget-test is a GATE, not a report: it replays a 60 s scripted
        timeline with vsync off and asserts p99 frame time under 16.6 ms, zero
        Gen0 collections in the window, and private bytes under 128 MB. It exits
        nonzero on failure.

        The footprint budget is graded on PRIVATE BYTES, not working set. §11
        originally said "working set under 60 MB"; both halves of that were
        wrong and the correction is measured. 60 MB is unreachable on a .NET
        plus OpenGL stack — the floor is 137 MB resident before the first frame
        is drawn, against a managed heap of 1.8 MB — and working set is elastic
        anyway: it wandered 46 MB during an idle soak while private bytes did
        not move. --budget-private rebases the ceiling from the command line.

        --soak renders for N hours, reporting working set, private bytes,
        handles, GC counts and frame time every five minutes, then prints a
        pass/fail on drift. It is looking for a leak, which a 60 s test cannot
        see. Drift is graded from a baseline latched at 20 minutes, because the
        GL driver allocates lazily for the first quarter of an hour.

        Batch dump renders the whole timeline in exact step mode and writes every
        Nth frame as a PNG, plus a manifest.

        --parity-mode a|b reads the exclusions, dump size and stride from
        tests/parity-config.json. Prefer it over the individual --no-* flags:
        the gate has to re-run with the exclusions that established its
        baseline, and a config file remembers those where a command line does
        not. Normally you would not run this by hand at all — use
        node tools/parity-gate.mjs --mode a, which drives both hosts and the
        comparison from the same config.

        The --no-* flags remain for diagnosis, and anything given explicitly on
        the command line overrides the config.

        --audio drives the visuals from WASAPI loopback on the default render
        endpoint: whatever the machine is playing. Without it the synthetic
        driver runs, which is what the parity gate uses and what works with no
        audio at all.

        --record-features writes real-audio Feature Frames in the same timeline
        format the harness reads, so you can capture a minute of real music and
        tune presets against it in the browser instead of against sliders.

        The path is relative to the CURRENT DIRECTORY and any missing folders
        are created. Recording runs on the analysis thread, one row per DSP hop
        (93.75 rows/s at 48 kHz), so the capture rate does not depend on how
        fast this machine renders. While it captures, a status line every two
        seconds reports level, tempo, confidence and the eight bands.
        """;
}
