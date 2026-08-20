using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ripple.Audio;
using Ripple.Features;
using Ripple.Presets;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Ripple.Render;

/// <summary>
/// The window, the GL 4.3 core context, and the frame loop.
///
/// Phase 1 scope: shaders, presets, the feedback chain, the Feature Frame UBO,
/// timeline replay and the synthetic driver. No audio, no DSP, no SMTC —
/// those are Phases 2 and 3, and the whole point of the synthetic driver is
/// that none of them are needed to finish this one.
/// </summary>
public sealed partial class GlHost : IDisposable
{
    private readonly RippleOptions _opt;

    private IWindow _window = null!;
    private GL _gl = null!;
    private IInputContext? _input;

    private ShaderLoader _shaders = null!;
    private PaletteLut _palette = null!;
    private ParticleSystem _particles = null!;
    private FeedbackChain _chain = null!;
    private PresetStore _presets = null!;

    private readonly DrawState _draw = new();
    private readonly HostState _host = new();
    private readonly SyntheticDriver _synth = new();
    private readonly Governor _governor = new();

    private FeatureTimeline? _timeline;
    private AudioEngine? _audio;
    private FeatureRecorder? _recorder;
    private FeatureFrame _frame;
    private FeatureFrame _audioFrame;      // last frame published by the analysis thread
    private uint _ubo;

    private int _presetIndex;
    private PresetValues _preset = null!;

    private double _tSec;
    private long _frameNo;
    private double _renderScale;
    private double _lastFrameMs;
    private bool _paused;

    /// <summary>Index of the timeline row the current frame was built from, or -1.</summary>
    private int _stepRow = -1;
    private int _stepNext;

    public GlHost(RippleOptions options)
    {
        _opt = options;
        _renderScale = options.RenderScale;
    }

    // ------------------------------------------------------------------ run

    public void Run()
    {
        var api = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core,
            ContextFlags.ForwardCompatible, new APIVersion(4, 3));

        var wo = WindowOptions.Default with
        {
            Size = new Vector2D<int>(_opt.Width, _opt.Height),
            Title = "Ripple",
            API = api,
            VSync = _opt.VSync && !_opt.Dump,
            IsVisible = !_opt.Dump,
            WindowBorder = WindowBorder.Resizable,
            ShouldSwapAutomatically = true,
        };

        _window = Window.Create(wo);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += OnFramebufferResize;
        _window.Closing += OnClosing;
        _window.Run();
    }

    private void OnLoad()
    {
        _gl = GL.GetApi(_window);

        Console.WriteLine($"[gl] {_gl.GetStringS(StringName.Version)}");
        Console.WriteLine($"[gl] {_gl.GetStringS(StringName.Renderer)}");

        // Desktop GL gates gl_PointSize behind this; GLES enables it always.
        // Without it the particle layer renders as single pixels and the parity
        // diff fails for a reason that has nothing to do with the shaders.
        _gl.Enable(EnableCap.ProgramPointSize);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

        _shaders = new ShaderLoader(_gl, _opt.ShaderRoot);
        _shaders.ReloadFailed += msg => Console.Error.WriteLine($"[shaders] reload failed, keeping previous:\n{msg}");
        _shaders.Reloaded += () => Console.WriteLine("[shaders] reloaded");

        _palette = new PaletteLut(_gl);
        _particles = new ParticleSystem(_gl);
        _chain = new FeedbackChain(_gl, _shaders, _palette, _particles);

        _presets = new PresetStore(_opt.PresetRoot, _opt.DataRoot);
        if (_presets.Library.Count == 0)
            throw new InvalidOperationException($"no presets found under {_opt.PresetRoot}");
        _presets.Reloaded += () => { SelectPreset(_presetIndex); Console.WriteLine("[presets] reloaded"); };

        CreateUniformBuffer();
        SelectPreset(ResolvePresetIndex(_opt.Preset));

        if (!string.IsNullOrEmpty(_opt.Timeline))
        {
            _timeline = FeatureTimeline.Load(_opt.Timeline);
            Console.WriteLine($"[timeline] {_timeline.Count} frames @ {_timeline.Rate} fps from {_opt.Timeline}");
        }

        if (_opt.Audio)
        {
            _audio = new AudioEngine();
            _audio.Start();

            _recordStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!string.IsNullOrEmpty(_opt.RecordFeatures))
            {
                // Record at the DSP's own frame rate, not the display's: one row per
                // analysis hop is what the timeline format means, and it makes the capture
                // independent of however fast this machine happens to render.
                var fmt = _audio.Format ?? new AudioFormat(48_000, 2);
                _recorder = new FeatureRecorder(fmt.FrameRate, _opt.RecordSeconds);
                _audio.AttachRecorder(_recorder);
                Console.WriteLine($"[record] {_opt.RecordSeconds:0.#}s at {fmt.FrameRate:0.#} fps "
                    + $"-> {_opt.RecordFeatures}");
                Console.WriteLine("[record] play something now; the window shows what is being captured");
            }
        }

        _governor.SetLevel(0);
        _chain.Resize(_opt.Width, _opt.Height, _renderScale);

        if (_opt.ShaderHotReload && !_opt.Dump)
        {
            _shaders.WatchForChanges();
            _presets.WatchForChanges();
            Console.WriteLine("[watch] shaders/ and presets/ hot-reload on save");
        }

        if (_opt.Dump)
        {
            RunDump();
            _window.Close();
            return;
        }

        SetUpInput();
        Console.WriteLine($"[ripple] {_preset.Id} · {_chain.ResolvedFamily} · {_opt.Width}x{_opt.Height} @ {_renderScale:0.##}x");
    }

    private void OnClosing()
    {
        // Audio first: the capture and analysis threads must stop before the GL objects
        // they feed go away.
        if (_audio is not null) ReportDspCost();
        _audio?.Dispose();
        _audio = null;

        _input?.Dispose();
        _chain?.Dispose();
        _particles?.Dispose();
        _palette?.Dispose();
        _shaders?.Dispose();
        _presets?.Dispose();
        if (_ubo != 0) _gl.DeleteBuffer(_ubo);
    }

    // --------------------------------------------------------------- the UBO

    private void CreateUniformBuffer()
    {
        if (Marshal.SizeOf<FeatureFrame>() != FeatureFrame.ByteSize)
            throw new InvalidOperationException(
                $"FeatureFrame is {Marshal.SizeOf<FeatureFrame>()} bytes, contract says {FeatureFrame.ByteSize}");

        _ubo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.UniformBuffer, _ubo);
        unsafe
        {
            _gl.BufferData(BufferTargetARB.UniformBuffer, FeatureFrame.ByteSize, null, BufferUsageARB.DynamicDraw);
        }
        // CONTRACTS §1: binding point 0. The shaders declare
        // layout(std140, binding = 0) directly — this host does not define
        // RIPPLE_WEBGL, so no glUniformBlockBinding call is needed.
        _gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _ubo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<float> FrameSpan()
        => MemoryMarshal.CreateSpan(ref Unsafe.As<FeatureFrame, float>(ref _frame), FeatureFrame.FloatCount);

    private void UploadFeatureFrame()
    {
        var f = FrameSpan();
        f[52] = f[53] = f[54] = f[55] = 0;   // uReserved MUST be zero-filled
        _gl.BindBuffer(BufferTargetARB.UniformBuffer, _ubo);
        unsafe
        {
            fixed (float* p = f)
                _gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, FeatureFrame.ByteSize, p);
        }
    }

    // ------------------------------------------------------------- presets

    private int ResolvePresetIndex(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return 0;
        if (int.TryParse(spec, out int n) && n >= 0 && n < _presets.Library.Count) return n;
        int byId = _presets.IndexOf(spec);
        if (byId >= 0) return byId;
        Console.Error.WriteLine($"[presets] no preset '{spec}', falling back to index 0");
        return 0;
    }

    private void SelectPreset(int index)
    {
        _presetIndex = Math.Clamp(index, 0, _presets.Library.Count - 1);
        _preset = _presets.Resolve(_presetIndex, _opt.Song);
        _palette.Build(_preset.PaletteStops);
        // The era id rides in the Feature Frame so shaders can branch on it.
        // On timeline replay the recorded value wins, exactly as in the harness.
        FrameSpan()[FF.Era] = _preset.EraId;
    }

    // ------------------------------------------------------------ the frame

    /// <summary>
    /// Assemble the Feature Frame. On replay the timeline is authoritative for
    /// everything the DSP owns; the host still owns time and resolution.
    /// </summary>
    private void BuildFeatureFrame(double dt)
    {
        var f = FrameSpan();

        if (_timeline is not null && _opt.Step)
        {
            _stepRow = Math.Min(_stepNext, Math.Max(0, _timeline.Count - 1));
            _timeline.CopyRow(_stepRow, f);
            _stepNext = Math.Min(_stepNext + 1, _timeline.Count);
        }
        else if (_timeline is not null)
        {
            // Interpolation-free but time-driven: useful for eyeballing a
            // recording at display rate. Never used for the parity diff.
            int i = (int)(_tSec * _timeline.Rate) % Math.Max(1, _timeline.Count);
            _stepRow = i;
            _timeline.CopyRow(i, f);
        }
        else if (_audio is not null)
        {
            // The render thread never blocks on audio: it takes whatever the analysis
            // thread last published, and holds the previous frame if nothing is ready.
            if (_audio.TryReadFrame(ref _audioFrame))
                _audioFrame.AsSpan().CopyTo(f);

            // §11: stale audio is silence. The capture may legitimately deliver no
            // callbacks at all while the endpoint is idle, so an absent frame must not
            // freeze the visuals mid-gesture — it must decay them.
            if (_audio.Idle) DecayTowardSilence(f, dt);

            // The preset's era still comes from the preset until Phase 3 wires up SMTC.
            f[FF.Era] = _preset.EraId;
        }
        else
        {
            _synth.Fill(f, dt, _tSec);
        }

        var rt = _chain.Targets;
        f[FF.Time] = (float)_tSec;
        f[FF.Dt] = (float)dt;
        f[FF.Width] = rt.W;
        f[FF.Height] = rt.H;
        f[FF.Aspect] = rt.W / (float)Math.Max(rt.H, 1);
        f[FF.RenderScale] = (float)_renderScale;
        f[52] = f[53] = f[54] = f[55] = 0;
    }

    /// <summary>Everything between "frame assembled" and "pixels drawn".</summary>
    private void DrawFrame(double dt)
    {
        var f = FrameSpan();
        UploadFeatureFrame();

        _draw.Reset(_preset);
        _draw.ApplyMapping(_preset, f);
        _governor.Apply(_draw);

        // Mode A drops the film grain along with the feedback carry. Both are
        // terms that cannot agree across two GL implementations by
        // construction: the feedback compounds, and the grain is a fract-chain
        // hash of vUv whose value is chaotic in the last bit of its input, so
        // an FMA contracted one way by the D3D compiler and another by the GL
        // driver decorrelates it completely.
        //
        // It carries no shader-math information — it is dither — but with the
        // feedback cleared the frame is dim enough that the grain becomes most
        // of the variance, and SSIM then measures noise against noise. Zeroing
        // it here is what makes the strict gate a test of the shaders.
        if (_opt.NoGrain) _draw.Grain = 0;
        if (_opt.NoParticles) _draw.PCount = 0;
        _host.AdvanceMotifs(dt, _draw, f);
        _host.AdvancePalette(dt, _draw, _preset.RotateByKey, f);

        _chain.Render(_draw, _host);
    }

    /// <summary>
    /// §11's idle behaviour, on the feature side: ramp the reactive fields down rather than
    /// freezing them. A held frame would leave the visuals mid-gesture — a bolt half struck,
    /// a bloom at full — which reads as a hang rather than as silence.
    /// </summary>
    private static void DecayTowardSilence(Span<float> f, double dt)
    {
        float k = (float)Math.Exp(-dt / 0.5);
        for (int i = FF.Rms; i <= FF.Air; i++) f[i] *= k;
        f[FF.Flux] *= k;
        f[FF.Onset] *= k;
        f[FF.ChromaFlux] *= k;
        f[FF.Novelty] *= k;
        f[FF.BpmConf] *= k;
    }

    /// <summary>
    /// Give up waiting rather than hanging. If nothing is playing there will be no frames
    /// at all, and a recorder that waits forever for them looks like a hung app — the
    /// endpoint may legitimately never deliver a callback (§2.3, failure mode 1).
    /// </summary>
    private void CheckRecordingDeadline()
    {
        if (_recorder is null || _recordingDone || _recordStart == 0) return;

        // The recorder is filled by the analysis thread; the render loop only watches.
        if (_recorder.Full) { FinishRecording("complete"); return; }

        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_recordStart).TotalSeconds;
        double budget = _opt.RecordSeconds * 3 + 10;
        if (elapsed < budget) return;

        FinishRecording(_recorder.Count == 0
            ? "no audio was captured — is anything playing?"
            : "deadline reached before the requested length");
    }

    private void FinishRecording(string why)
    {
        if (_recordingDone) return;
        _recordingDone = true;

        if (_recorder!.Count == 0)
        {
            Console.Error.WriteLine($"[record] nothing recorded: {why}");
            _window.Close();
            return;
        }

        {
            var fmt = _audio!.Format ?? new AudioFormat(48_000, 2);
            _recorder.Write(_opt.RecordFeatures!,
                $"Captured from {_audio.DeviceName} via WASAPI loopback, one row per "
                + $"analysis hop. Dropped {_audio.DroppedSamples} samples to ring overrun.",
                fmt, _audio.DeviceName);
            Console.WriteLine($"[record] wrote {_recorder.Count} frames "
                + $"({_recorder.Seconds:0.#}s) to {_opt.RecordFeatures} — {why}");
            if (_audio.DroppedSamples > 0)
                Console.WriteLine($"[record] {_audio.DroppedSamples} samples were dropped to "
                    + "ring overrun: the analysis thread could not keep up with capture.");
            Console.WriteLine("[record] done — closing");
            _window.Close();
        }
    }

    private bool _recordingDone;
    private long _recordStart;

    /// <summary>
    /// §11 budgets the analysis chain at "under 1% of one core". Print what it actually
    /// cost, rather than asserting it in a comment.
    /// </summary>
    private void ReportDspCost()
    {
        var a = _audio!;
        if (a.AnalyzedFrames == 0)
        {
            Console.WriteLine("[dsp] no audio was analyzed");
            return;
        }
        Console.WriteLine($"[dsp] {a.AnalyzedFrames} hops, {a.DspMsPerHop:F3} ms/hop, "
            + $"{a.DspCoreFraction * 100:F2}% of one core "
            + $"(budget 1.00%), {a.DroppedSamples} samples dropped, "
            + $"{a.Restarts} capture restart(s)");
        foreach (var (name, ms) in a.StageCostMs())
            Console.WriteLine($"[dsp]   {name,-14} {ms,8:F4} ms/hop");
    }

    private void OnRender(double _)
    {
        if (_opt.Dump) return;   // the dump loop drives itself

        _shaders.PollReload();
        _presets.PollReload();
        CheckRecordingDeadline();

        double dt;
        if (_timeline is not null && _opt.Step)
        {
            // Step mode on screen: one row per displayed frame, dt pinned to
            // the recording's rate. Same path the dump takes, so what you watch
            // is what lands in the PNGs.
            dt = 1.0 / _timeline.Rate;
            _tSec = _stepNext / _timeline.Rate;
        }
        else
        {
            dt = Math.Clamp(_window.Time - _tSec, 0.0004, 0.05);
        }

        if (_paused) { _chain.Present(_window.FramebufferSize.X, _window.FramebufferSize.Y); return; }

        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        BuildFeatureFrame(dt);
        DrawFrame(dt);

        long cadence = (_timeline is not null && _opt.Step) ? _stepRow : _frameNo;
        if (cadence % 3 == 0)
            _host.MeasureLuminance(_gl, _chain.Targets, dt * 3, _tSec, _draw);

        _chain.Present(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        _lastFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (!(_timeline is not null && _opt.Step)) _tSec += dt;
        _frameNo++;
    }

    private void OnFramebufferResize(Vector2D<int> size)
    {
        if (size.X <= 0 || size.Y <= 0) return;
        if (_opt.Dump) return;   // a dump pins its own grid
        _chain.Resize(size.X, size.Y, _renderScale);
    }

    // --------------------------------------------------------------- input

    private void SetUpInput()
    {
        _input = _window.CreateInput();
        foreach (var kb in _input.Keyboards) kb.KeyDown += OnKeyDown;
    }

    private void OnKeyDown(IKeyboard kb, Key key, int _)
    {
        switch (key)
        {
            case Key.Escape:
                _window.Close();
                break;
            case Key.Right:
                SelectPreset((_presetIndex + 1) % _presets.Library.Count);
                Console.WriteLine($"[preset] {_preset.Id} — {_preset.Name}");
                break;
            case Key.Left:
                SelectPreset((_presetIndex - 1 + _presets.Library.Count) % _presets.Library.Count);
                Console.WriteLine($"[preset] {_preset.Id} — {_preset.Name}");
                break;
            case Key.Space:
                _paused = !_paused;
                break;
            case Key.D:
                Console.WriteLine($"[hud] {_preset.Id} · {_chain.ResolvedFamily} · "
                    + $"{_chain.Targets.Pw}x{_chain.Targets.Ph} · {_lastFrameMs:0.00} ms · "
                    + $"decay {_draw.Decay:0.0000} · limiter {_host.SafeScale:0.000} · flash {_host.FlashRate:0.0} Hz");
                break;
        }
    }

    public void Dispose() => _window?.Dispose();
}
