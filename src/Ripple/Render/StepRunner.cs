using System.Diagnostics;
using System.Text.Json;
using Silk.NET.OpenGL;

namespace Ripple.Render;

/// <summary>
/// Deterministic step replay and the batch frame dump — the native half of the
/// Phase 1 parity gate.
///
/// The mirror of this code is the PARITY block in harness/index.html. Both
/// pin every host-side input to a frame: dt, elapsed time, target size, render
/// scale, governor tier, feedback history and every RNG. Change one side and
/// you must change the other, or the SSIM report is the only thing that will
/// notice.
/// </summary>
public sealed partial class GlHost
{
    /// <summary>
    /// Put the host back to a cold start. Every accumulator listed here feeds
    /// the image, so anything missed makes the diff depend on whatever the
    /// process happened to be doing before the run began.
    /// </summary>
    private void ResetForDeterministicRun()
    {
        _tSec = 0;
        _frameNo = 0;
        _stepRow = -1;
        _stepNext = 0;

        _host.Reset();
        _draw.PalRot = 0;          // the one DrawState field Reset() does not clear

        _governor.Auto = false;
        _governor.SetLevel(0);
        _renderScale = _opt.RenderScale;

        _chain.Resize(_opt.Width, _opt.Height, _renderScale);
        _chain.Targets.ClearFeedback();
    }

    /// <summary>
    /// Exactly one deterministic frame: dt is 1/rate, time is index/rate, and
    /// the luminance loop runs off the step index rather than a frame counter
    /// that started when the process did.
    /// </summary>
    private void StepOnce()
    {
        double rate = _timeline!.Rate;
        double dt = 1.0 / rate;
        _tSec = _stepNext / rate;          // _stepNext is the row about to render

        // Feedback excluded: nothing carries between frames. Clearing here rather
        // than in ResetForDeterministicRun also re-pins RT.cur to 0, so the
        // ping-pong parity cannot drift between the hosts either.
        if (_opt.NoFeedback) _chain.Targets.ClearFeedback();

        BuildFeatureFrame(dt);             // copies row _stepNext, advances it
        DrawFrame(dt);

        if (_stepRow % 3 == 0)
            _host.MeasureLuminance(_gl, _chain.Targets, dt * 3, _tSec, _draw);

        _frameNo++;
    }

    /// <summary>
    /// Render the whole timeline for one or every preset and write every
    /// stride-th frame as a PNG.
    ///
    /// Every frame is rendered; only the writing is strided. The feedback
    /// buffer IS the history, so skipping a render would change every frame
    /// after it and the two hosts would be comparing different runs.
    /// </summary>
    private void RunDump()
    {
        if (_timeline is null) throw new InvalidOperationException("--dump needs a timeline");
        if (_timeline.Count == 0) throw new InvalidOperationException("timeline has no frames");

        int stride = Math.Max(1, _opt.Stride);
        var indices = _opt.DumpAll
            ? Enumerable.Range(0, _presets.Library.Count).ToArray()
            : [_presetIndex];

        var outRoot = Path.GetFullPath(_opt.DumpOut);
        Directory.CreateDirectory(outRoot);

        int w = _opt.Width, h = _opt.Height;
        var pixels = new byte[w * h * 4];
        var sw = Stopwatch.StartNew();
        int written = 0;

        var excluded = new List<string>();
        if (_opt.NoFeedback) excluded.Add("feedback");
        if (_opt.NoGrain) excluded.Add("grain");
        if (_opt.NoParticles) excluded.Add("particles");

        Console.WriteLine($"[dump] {indices.Length} preset(s) × {_timeline.Count} frames "
            + $"@ {w}x{h} scale {_renderScale:0.##}, writing every {stride}, "
            + $"excluding {(excluded.Count == 0 ? "nothing" : string.Join(" + ", excluded))}");

        foreach (int pi in indices)
        {
            SelectPreset(pi);
            ResetForDeterministicRun();

            var dir = Path.Combine(outRoot, _preset.Id);
            Directory.CreateDirectory(dir);

            var trace = _opt.Trace ? new List<double[]>(_timeline.Count) : null;

            for (int i = 0; i < _timeline.Count; i++)
            {
                StepOnce();
                trace?.Add(TraceRow());
                if (i % stride != 0) continue;

                ReadFinal(pixels, w, h);
                // frame_NNNNN.png, matching the harness byte for byte in the
                // name as well as the pixels: compare-ssim.mjs pairs files by
                // name, so a naming difference reads as a missing frame.
                PngWriter.WriteRgbaBottomUp(Path.Combine(dir, $"frame_{i:D5}.png"), pixels, w, h);
                written++;
            }

            if (trace is not null)
                File.WriteAllText(Path.Combine(dir, "trace.json"), JsonSerializer.Serialize(
                    new { kind = "ripple-draw-trace", host = "native", preset = _preset.Id,
                          keys = TraceKeys, rows = trace }));

            WriteManifest(dir, stride);
            Console.WriteLine($"[dump] {_preset.Id,-18} {_chain.ResolvedFamily,-12} "
                + $"{_timeline.Count} frames, {(_timeline.Count + stride - 1) / stride} png");
        }

        Console.WriteLine($"[dump] {written} frames written to {outRoot} in {sw.Elapsed.TotalSeconds:0.0}s");
    }

    /// <summary>
    /// The scalars a frame's appearance actually depends on, in a fixed order.
    /// Mirrored by __ripple.trace() in the harness — add to one and you must
    /// add to the other, in the same position.
    /// </summary>
    internal static readonly string[] TraceKeys =
    [
        "decay", "zoom", "rotate", "warpAmount", "warpScale", "bassGain", "symmetry",
        "paint", "centerPull", "onsetDecay",
        "iterations", "escapeRadius", "orbitRadius", "beatMorph",
        "warp", "scale", "flow", "cells", "rim", "iridescence", "mask", "octaves", "rimWidth",
        "trapGain", "zoomPeriod", "zoomDepth", "fold", "line",
        "boltGain", "boltThreshold", "roseGain", "roseThreshold",
        "motifOpacity", "motifGain",
        "pCount", "pCurl", "pLife", "pSize", "pEmit", "pRise",
        "bloomThreshold", "bloomGain", "bloomTaps", "bloomKnee",
        "grain", "vignette", "exposure", "saturate",
        "chromaBlend", "spiral", "palRot",
        "maxLumDelta", "maxBright",
        "rose", "bolt", "safeMean", "safeScale", "hueRate",
    ];

    private double[] TraceRow()
    {
        var d = _draw; var h = _host;
        return
        [
            d.Decay, d.Zoom, d.Rotate, d.WarpAmount, d.WarpScale, d.BassGain, d.Symmetry,
            d.Paint, d.CenterPull, d.OnsetDecay,
            d.Iterations, d.EscapeRadius, d.OrbitRadius, d.BeatMorph,
            d.Warp, d.Scale, d.Flow, d.Cells, d.Rim, d.Iridescence, d.Mask, d.Octaves, d.RimWidth,
            d.TrapGain, d.ZoomPeriod, d.ZoomDepth, d.Fold, d.Line,
            d.BoltGain, d.BoltThreshold, d.RoseGain, d.RoseThreshold,
            d.MotifOpacity, d.MotifGain,
            d.PCount, d.PCurl, d.PLife, d.PSize, d.PEmit, d.PRise,
            d.BloomThreshold, d.BloomGain, d.BloomTaps, d.BloomKnee,
            d.Grain, d.Vignette, d.Exposure, d.Saturate,
            d.ChromaBlend, d.Spiral, d.PalRot,
            d.MaxLumDelta, d.MaxBright,
            h.Rose, h.Bolt, h.SafeMean, h.SafeScale, h.HueRate,
        ];
    }

    /// <summary>
    /// Read the composite target, not the back buffer: no window compositor in
    /// the path, and it works with the window hidden.
    /// </summary>
    private void ReadFinal(byte[] into, int w, int h)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _chain.Targets.FinalFbo);
        unsafe
        {
            fixed (byte* p = into)
                _gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void WriteManifest(string dir, int stride)
    {
        var manifest = new
        {
            schemaVersion = 1,
            kind = "ripple-parity-dump",
            host = "native",
            preset = _preset.Id,
            presetName = _preset.Name,
            fractal = _chain.ResolvedFamily,
            width = _opt.Width,
            height = _opt.Height,
            renderScale = _renderScale,
            rate = _timeline!.Rate,
            frames = _timeline.Count,
            stride,
            mode = "step",
            // The full exclusion set, so compare-ssim.mjs can refuse to grade two
            // dumps that were never comparable — and so a dump directory on its own
            // says which experiment produced it.
            feedback = !_opt.NoFeedback,
            grain = !_opt.NoGrain,
            particles = !_opt.NoParticles,
            gateMode = string.IsNullOrEmpty(_opt.ParityMode)
                ? (_opt.NoFeedback ? "A" : "B")
                : _opt.ParityMode.ToUpperInvariant(),
            rngParticleSeed = Mulberry32.ParticleSeed,
            rngMotifSeed = Mulberry32.MotifSeed,
            colorBuffer = "RGBA16F",
            gl = _gl.GetStringS(StringName.Version),
            renderer = _gl.GetStringS(StringName.Renderer),
            timeline = Path.GetFileName(_timeline.Source),
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }
}
