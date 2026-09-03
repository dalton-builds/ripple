using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ripple.Audio;
using Ripple.Features;
using Ripple.Presets;
using Ripple.Ui;
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

        // The config is the DEFAULT and the command line is the OVERRIDE — but ONLY for an
        // interactive run. Every measurement run reads stock defaults instead, because a
        // parity gate or a budget gate that picked up whatever is in someone's %APPDATA%
        // would silently stop being a gate. The budget run is the sharpest case: the config
        // enables audio by default, which would start the analysis thread and put it in
        // competition with the render loop being measured, on top of a timeline that makes
        // its output unused.
        bool interactive = !options.Dump
                        && string.IsNullOrEmpty(options.ParityMode)
                        && !options.BudgetTest
                        && options.SoakHours <= 0;

        _cfg = interactive ? RippleConfig.LoadOrCreate() : new RippleConfig();
        if (interactive) ApplyConfigDefaults();

        _renderScale = _opt.RenderScale;
    }

    /// <summary>
    /// Fold config values into the options, for every field the caller did not set
    /// explicitly. The *Specified flags already exist for the parity profile's benefit and
    /// mean exactly the right thing here too.
    /// </summary>
    private void ApplyConfigDefaults()
    {
        if (!_opt.SizeSpecified) { _opt.Width = _cfg.Width; _opt.Height = _cfg.Height; }
        if (!_opt.ScaleSpecified) _opt.RenderScale = _cfg.RenderScale;
        if (string.IsNullOrEmpty(_opt.Preset)) _opt.Preset = _cfg.Preset;

        // Audio defaults ON in the config and OFF on the command line. First run with no
        // flags has to react to sound, or it is not the thing the prototype gate describes.
        if (!_opt.Audio) _opt.Audio = _cfg.Audio;
        _opt.VSync = _opt.VSync && _cfg.VSync;

        // --windowed overrides the config for this run only. Nothing writes _cfg back to disk
        // from here, so a test session cannot leave the saved look changed behind it.
        if (_opt.Windowed) _cfg.Borderless = false;
    }

    // ------------------------------------------------------------------ run

    public void Run()
    {
        RegisterWindowPlatform();

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

    /// <summary>
    /// Register the GLFW windowing and input backends EXPLICITLY.
    ///
    /// Silk.NET normally discovers its platform by scanning loaded assemblies, and that scan
    /// does not survive a single-file publish: the assemblies are inside the bundle rather
    /// than beside the exe, nothing has forced them to load by the time `Window.Create` runs,
    /// and the failure is `PlatformNotSupportedException: Couldn't find a suitable window
    /// platform (GlfwPlatform - not applicable)` — from a build that works perfectly when run
    /// from `bin/`. It cost a full publish cycle to find, because it cannot reproduce in
    /// development.
    ///
    /// Calling the registrars takes a hard reference to both backends, so they are in the
    /// bundle and registered before anything asks. Harmless when running non-published:
    /// registration is idempotent.
    /// </summary>
    private static void RegisterWindowPlatform()
    {
        try
        {
            Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
            Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();
        }
        catch (Exception ex)
        {
            // If this throws, Window.Create is about to give a much better message.
            Console.Error.WriteLine($"[gl] platform registration: {ex.Message}");
        }
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
                    + $"-> {Path.GetFullPath(_opt.RecordFeatures)}");
                if (!string.IsNullOrEmpty(_opt.RecordLabel))
                    Console.WriteLine($"[record] label: {_opt.RecordLabel}");
                Console.WriteLine("[record] recording starts when audio arrives — start playback now.");
                Console.WriteLine($"[record] {"level",6}  {"bands",8}   tempo / key / percussivity / width");
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

        if (Measuring)
        {
            // A measurement run gets the GPU timer and nothing else. Hotkeys, the tray icon
            // and config hot-reload would all put work and allocation inside the window
            // being measured, and the governor stepping mid-run would change what the p99 is
            // a p99 of.
            _gpuTimer = new GpuTimer(_gl);
            _governor.Auto = false;
            _governor.SetTargetFps(60);
            StartMeasurementRun();
        }
        else
        {
            StartDeskLayer();
            StartMetadata();
        }

        Console.WriteLine($"[ripple] {_preset.Id} · {_chain.ResolvedFamily} · {_opt.Width}x{_opt.Height} @ {_renderScale:0.##}x");
    }

    private void OnClosing()
    {
        StopMetadata();

        // The desk layer next: it owns the geometry save, and saving it after the window is
        // torn down reads back a destroyed HWND and persists zeros.
        StopDeskLayer();

        // Audio next: the capture and analysis threads must stop before the GL objects
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

    /// <summary>
    /// Select a preset, optionally with a song mode merged over it.
    ///
    /// <paramref name="songMode"/> null means "whatever the command line asked for", which is
    /// the Phase 1 behaviour and what every non-metadata caller wants. The metadata layer
    /// passes an explicit key — including explicitly none, when a track has no mode — so it
    /// can clear a previous track's mode rather than leaving it applied.
    /// </summary>
    private void SelectPreset(int index, string? songMode = null)
    {
        _presetIndex = Math.Clamp(index, 0, _presets.Library.Count - 1);
        _preset = _presets.Resolve(_presetIndex, songMode ?? _opt.Song);
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

            MaybeAutoAdvance(f);
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
        ReportCaptureProgress();
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
                fmt, _audio.DeviceName, _opt.RecordLabel);
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
    private long _lastProgressTicks;

    /// <summary>
    /// A status line every two seconds while capturing. §15 lists a diagnostic HUD in
    /// Phase 2 and this is it, in the form that matters for a capture session: you can see
    /// whether the level is sane, whether all eight bands have content, and whether the
    /// tempo locked — while there is still time to stop and fix it.
    /// </summary>
    private void ReportCaptureProgress()
    {
        if (_audio is null || _recorder is null) return;

        double since = _lastProgressTicks == 0
            ? double.PositiveInfinity
            : System.Diagnostics.Stopwatch.GetElapsedTime(_lastProgressTicks).TotalSeconds;
        if (since < 2.0) return;
        _lastProgressTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        string warn = "";
        if (!_audio.Healthy) warn = "   <- NO AUDIO";
        else if (_audio.DroppedSamples > 0) warn = $"   <- {_audio.DroppedSamples} samples dropped";

        Console.WriteLine($"[record] {_recorder.Seconds,5:0.0}/{_opt.RecordSeconds:0.#}s  "
            + $"{_audio.Readout()}{warn}");
    }

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
        PollConfig();
        CheckRecordingDeadline();

        // Both run on the render thread on purpose: a hotkey may resize a render target and
        // a tray command may rebuild a palette, and neither is legal from another thread
        // with no GL context bound.
        _hotkeys?.Drain(DoAction);
        _tray?.Drain(OnTrayCommand);

        // Track changes are picked up here, before dt exists, because a new track may change
        // the preset and that has to happen before the frame is built. The caption's own
        // clock advances later, with the real dt.
        PollMetadata();

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

        if (!PumpIdleState(dt)) return;

        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _gpuTimer?.Begin();

        BuildFeatureFrame(dt);
        DrawFrame(dt);

        long cadence = (_timeline is not null && _opt.Step) ? _stepRow : _frameNo;
        if (cadence % 3 == 0)
            _host.MeasureLuminance(_gl, _chain.Targets, dt * 3, _tSec, _draw);

        _chain.Present(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        // AFTER the composite blit, so chrome is never part of the luminance §12 measures.
        _caption?.Advance(dt);
        _caption?.Draw(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        _lastFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        // GPU time when the driver gives it to us, wall clock otherwise. See GpuTimer for
        // why the difference matters to the control loop.
        double gpuMs = _gpuTimer?.End() ?? -1;
        bool gpuTimed = gpuMs >= 0;
        double measured = gpuTimed ? gpuMs : _lastFrameMs;
        _governor.Tick(dt, measured, gpuTimed);

        if (Measuring && MeasurementTick(measured)) { _window.Close(); return; }

        MaybeHud(dt);

        if (!(_timeline is not null && _opt.Step)) _tSec += dt;
        _frameNo++;
    }

    /// <summary>True for the two non-interactive measurement runs (§11's budget and soak).</summary>
    private bool Measuring => _opt.BudgetTest || _opt.SoakHours > 0;

    /// <summary>
    /// §11's idle handling, on the render side. Returns false when this frame should not be
    /// drawn at all.
    ///
    /// The HIDDEN case blocks on the event loop rather than sleeping in a spin — the plan is
    /// explicit that 1 fps is not good enough, and it is right: a 1 fps loop still wakes the
    /// CPU, still holds the context, and still shows up in a power profile. Thread.Sleep on
    /// the QUIET path is fine by contrast, because the window is visible and we do intend to
    /// come back in a tenth of a second.
    /// </summary>
    private bool PumpIdleState(double dt)
    {
        bool visible = _visible && !WindowChrome.IsMinimized(_hwnd);
        // SMTC pause is INSTANT; §11's RMS idle needs three seconds of silence to reach the
        // same conclusion. When metadata says paused there is nothing to wait for, and the
        // difference between the two is the difference between "it noticed" and "it reacted".
        bool audioIdle = (_audio?.Idle ?? false) || _metaPaused;

        bool draw = _idle.ShouldRender(dt, audioIdle, visible);
        _host.IdleDim = _idle.Dim;
        _host.MasterIntensity = _intensity;

        if (_idle.Current == IdleController.State.Hidden)
        {
            // BLOCK, do not poll. Silk.NET's DoEvents maps to glfwPollEvents, which returns
            // immediately — calling it in a Sleep loop is exactly the 1 fps spin §11 rules
            // out. glfwWaitEventsTimeout parks the thread in the OS until a message arrives,
            // so a restore or a tray click wakes it instantly and an idle minute costs
            // nothing. The timeout is only a safety valve against a lost wakeup.
            WaitForEvents(0.25);
            return false;
        }

        if (!draw && _idle.SleepSeconds > 0)
        {
            Thread.Sleep((int)Math.Clamp(_idle.SleepSeconds * 1000, 1, 100));
            return false;
        }

        return draw;
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

    /// <summary>
    /// Window-local keys. These are the plan's bindings as bare letters, which is what they
    /// can be once the window has focus — see the note in <see cref="Ui.HotKeys"/> for why
    /// the unprefixed letters are NOT registered system-wide. Both paths funnel into
    /// DoAction, so a binding cannot mean two things depending on how it arrived.
    /// </summary>
    private void OnKeyDown(IKeyboard kb, Key key, int _)
    {
        switch (key)
        {
            case Key.Escape: _window.Close(); break;
            case Key.Q: DoAction(HotkeyAction.Quit); break;

            case Key.Right:
            case Key.N: DoAction(HotkeyAction.NextPreset); break;

            case Key.Left: DoAction(HotkeyAction.PrevPreset); break;

            case Key.Space: DoAction(HotkeyAction.TogglePause); break;
            case Key.F: DoAction(HotkeyAction.Fullscreen); break;
            case Key.L: DoAction(HotkeyAction.LockPreset); break;
            case Key.S: DoAction(HotkeyAction.Screenshot); break;
            case Key.LeftBracket: DoAction(HotkeyAction.IntensityDown); break;
            case Key.RightBracket: DoAction(HotkeyAction.IntensityUp); break;
            case Key.V: DoAction(HotkeyAction.ToggleVisible); break;

            case Key.D:
                // D both toggles the periodic HUD and prints one line immediately, so a
                // single press still answers "what is it doing right now".
                DoAction(HotkeyAction.ToggleHud);
                Console.WriteLine($"[hud] {_preset.Id} · {_chain.ResolvedFamily} · "
                    + $"{_chain.Targets.Pw}x{_chain.Targets.Ph} · {_lastFrameMs:0.00} ms · "
                    + $"tier {_governor.Level} · {_idle.Current} · "
                    + $"decay {_draw.Decay:0.0000} · limiter {_host.SafeScale:0.000} · "
                    + $"flash {_host.FlashRate:0.0} Hz · intensity {_intensity:0.00}");
                break;
        }
    }

    public void Dispose() => _window?.Dispose();
}
