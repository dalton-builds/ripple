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

        return o;
    }

    public sealed class HelpRequested : Exception;

    public const string Usage = """
        Ripple — Phase 1 native shell

          Ripple [--audio] [--preset <id|index>] [--song <title>] [--size 960x540]
                 [--scale 0.75] [--timeline <file.json> [--step]] [--no-vsync]
                 [--no-hot-reload]

          Ripple --record-features <out.json> [--record-seconds 60]
                 [--record-label "what this capture is of"]

          Ripple --dump --timeline <file.json> --out <dir> [--all]
                 [--parity-mode a|b] [--preset <id|index>] [--stride N]
                 [--size 960x540] [--scale 0.75] [--trace]

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
