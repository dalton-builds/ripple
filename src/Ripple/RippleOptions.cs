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
                case "--no-feedback": o.NoFeedback = true; break;
                case "--no-particles": o.NoParticles = true; break;
                case "--trace": o.Trace = true; break;
                case "--scale": o.RenderScale = double.Parse(Next(a), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--stride": o.Stride = int.Parse(Next(a), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--dump": o.Dump = true; o.Step = true; break;
                case "--out": o.DumpOut = Next(a); break;
                case "--all": o.DumpAll = true; break;
                case "--size":
                {
                    var parts = Next(a).Split('x', 'X');
                    if (parts.Length != 2) throw new ArgumentException("--size wants WxH, e.g. 960x540");
                    o.Width = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                    o.Height = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                }
                case "-h":
                case "--help":
                    throw new HelpRequested();
                default:
                    throw new ArgumentException($"unknown argument: {a}");
            }
        }

        if (o.Dump && string.IsNullOrEmpty(o.Timeline))
            throw new ArgumentException("--dump needs --timeline: the parity gate replays a recorded timeline");
        if (o.Step && string.IsNullOrEmpty(o.Timeline))
            throw new ArgumentException("--step needs --timeline");

        return o;
    }

    public sealed class HelpRequested : Exception;

    public const string Usage = """
        Ripple — Phase 1 native shell

          Ripple [--preset <id|index>] [--song <title>] [--size 960x540] [--scale 0.75]
                 [--timeline <file.json> [--step]] [--no-vsync] [--no-hot-reload]

          Ripple --dump --timeline <file.json> --out <dir> [--all] [--no-feedback]
                 [--preset <id|index>] [--stride N] [--size 960x540] [--scale 0.75]

        Batch dump renders the whole timeline in exact step mode and writes every
        Nth frame as a PNG, plus a manifest. Point tools/compare-ssim.mjs at this
        directory and the harness's to run the Phase 1 parity gate.

        --no-feedback is gate mode A: the feedback chain is cleared before every
        frame, so nothing accumulates and the diff measures shader math only.
        Without it the dump is mode B, where cross-host divergence compounds by
        design and the SSIM curve is informational rather than a pass/fail.
        """;
}
