using System.Diagnostics;
using Ripple.Ui;
using Silk.NET.Windowing;

namespace Ripple.Render;

/// <summary>
/// The part that makes Ripple leaveable-running rather than a demo you launch and watch:
/// window chrome, hotkeys, the tray icon, config hot-reload, the governor's control loop and
/// §11's idle states.
///
/// Separate file, same class. The Phase 1 half of <see cref="GlHost"/> is the renderer and is
/// worth reading on its own; none of this belongs in the middle of it.
/// </summary>
public sealed partial class GlHost
{
    private RippleConfig _cfg = null!;
    private FileSystemWatcher? _cfgWatcher;
    private volatile bool _cfgDirty;

    private HotKeys? _hotkeys;
    private TrayIcon? _tray;
    private IdleController _idle = new();
    private GpuTimer? _gpuTimer;

    private IntPtr _hwnd;
    private bool _visible = true;
    private bool _fullscreen;
    private bool _presetLocked;
    private bool _hudOn;

    /// <summary>Master intensity (§12), hotkey-adjustable. Only ever scales DOWN.</summary>
    private double _intensity = 1.0;

    private double _lastNoveltyAdvance = -1e9;
    private int _windowedX, _windowedY, _windowedW, _windowedH;

    private double _hudTimer;

    /// <summary>Non-null only during a --budget-test run.</summary>
    private BudgetGate? _budget;

    /// <summary>Set when the budget gate has failed, so Program can exit nonzero.</summary>
    public bool BudgetFailed { get; private set; }

    private long _runStartTicks;
    private double _lastSoakReport;

    /// <summary>
    /// The measurement runs, both of which end by closing the window. Neither uses the desk
    /// layer — no hotkeys, no tray icon, no config — because a measurement that depends on
    /// what is in %APPDATA% is not a measurement.
    /// </summary>
    private void StartMeasurementRun()
    {
        _runStartTicks = Stopwatch.GetTimestamp();

        if (_opt.BudgetTest)
        {
            _budget = _opt.BudgetPrivateMb > 0
                ? new BudgetGate(_opt.BudgetPrivateMb * 1024 * 1024)
                : new BudgetGate();
            Console.WriteLine($"[budget] {_opt.BudgetSeconds:0.#}s at "
                + $"{_opt.Width}x{_opt.Height} @ {_renderScale:0.##}x, "
                + $"preset {_preset.Id}, vsync off, "
                + $"warm-up {BudgetGate.WarmupFrames} frames");
        }

        if (_opt.SoakHours > 0)
        {
            Console.WriteLine($"[soak] {_opt.SoakHours:0.##} h at {_opt.Width}x{_opt.Height} "
                + $"@ {_renderScale:0.##}x, preset {_preset.Id}");
            Console.WriteLine($"[soak] {"elapsed",10} {"wsMB",9} {"privMB",9} "
                + $"{"handles",7} {"gen0",6} {"gen2",6} {"p50ms",8} {"tier",5}");
            ReportSoak(force: true);
        }
    }

    /// <summary>
    /// Per-frame bookkeeping for the two measurement runs, and the thing that ends them.
    /// Returns true when the run is over and the window should close.
    /// </summary>
    private bool MeasurementTick(double frameMs)
    {
        double elapsed = Stopwatch.GetElapsedTime(_runStartTicks).TotalSeconds;

        if (_budget is not null)
        {
            _budget.Sample(frameMs);
            if (elapsed < _opt.BudgetSeconds) return false;

            var result = _budget.Evaluate();
            BudgetGate.Report(result, _gpuTimer?.Available == true,
                _budget.StartupWorkingSet, _budget.StartupPrivateBytes, Console.Out);
            BudgetFailed = !result.Pass;
            return true;
        }

        if (_opt.SoakHours > 0)
        {
            _soakFrameMs.Add(frameMs);
            if (elapsed - _lastSoakReport >= SoakReportSeconds)
            {
                _lastSoakReport = elapsed;
                ReportSoak();
            }
            if (elapsed >= _opt.SoakHours * 3600) { ReportSoak(force: true); ReportSoakVerdict(); return true; }
        }

        return false;
    }

    /// <summary>Report every 5 minutes: frequent enough to see a trend, sparse enough to read.</summary>
    private const double SoakReportSeconds = 300;

    private readonly List<double> _soakFrameMs = [];
    private long _soakBaselinePrivate;
    private readonly Process _self = Process.GetCurrentProcess();

    /// <summary>
    /// One line per interval.
    ///
    /// PRIVATE BYTES IS THE LEAK INDICATOR, not working set, and the distinction decides
    /// whether an 8-hour verdict means anything. Working set is elastic — Windows trims and
    /// regrows it against whole-machine pressure — so it is not a property of this process
    /// alone. Measured over three minutes of an idle soak it wandered 218 -> 205 -> 179 ->
    /// 172 MB while private bytes sat at 117 MB and the handle count did not move. Graded on
    /// working set that run "leaked 80 MB and then gave it back"; graded on private bytes it
    /// was flat, which is the truth.
    ///
    /// Handles are here for the same reason: a GL or Win32 object leaked once per frame shows
    /// up in the handle count long before it shows up in bytes.
    /// </summary>
    private void ReportSoak(bool force = false)
    {
        double elapsed = Stopwatch.GetElapsedTime(_runStartTicks).TotalSeconds;
        long ws = Environment.WorkingSet;

        long priv = 0;
        int handles = 0;
        try
        {
            _self.Refresh();       // without this, every reading is the value at process start
            priv = _self.PrivateMemorySize64;
            handles = _self.HandleCount;
        }
        catch (Exception) { /* a missing counter must not end an 8-hour run */ }

        if (_soakBaselinePrivate == 0) _soakBaselinePrivate = priv;

        // Latch the settled baseline once, at the first report past the settling window.
        if (_soakSettledPrivate == 0 && elapsed >= SoakSettleSeconds) _soakSettledPrivate = priv;

        double p50 = 0;
        if (_soakFrameMs.Count > 0)
        {
            var v = _soakFrameMs.ToArray();
            Array.Sort(v);
            p50 = v[v.Length / 2];
            // Cleared each interval: the question is what the LAST five minutes cost, not
            // what the average since launch was — an average hides a slow decline.
            _soakFrameMs.Clear();
        }

        // Alignment goes BEFORE the colon: {value,width:format}. Written the other way round
        // the ",10" is parsed as part of the TimeSpan format string and throws FormatException
        // on the very first report.
        Console.WriteLine($"[soak] {TimeSpan.FromSeconds(elapsed),10:hh\\:mm\\:ss} "
            + $"{ws / (1024.0 * 1024),9:F1} {priv / (1024.0 * 1024),9:F1} "
            + $"{handles,7} {GC.CollectionCount(0),6} {GC.CollectionCount(2),6} "
            + $"{p50,8:F2} {_governor.Level,5}"
            + $"   {(priv - _soakBaselinePrivate) / (1024.0 * 1024):+0.0;-0.0} MB private drift");
    }

    /// <summary>
    /// The soak's pass/fail, printed once at the end.
    ///
    /// Graded from the SETTLED baseline, not from process start. The GL driver allocates
    /// lazily for the first ten minutes or so — measured an 11 MB step at 5 min and a further
    /// 27 MB step at 15 min before flattening — and counting that as drift would fail every
    /// run for a reason that has nothing to do with an 8-hour leak. So the baseline is taken
    /// after the settling window and the requirement applies from there.
    /// </summary>
    private void ReportSoakVerdict()
    {
        long priv = 0;
        try { _self.Refresh(); priv = _self.PrivateMemorySize64; }
        catch (Exception) { }

        double hours = Stopwatch.GetElapsedTime(_runStartTicks).TotalHours;
        long drift = priv - (_soakSettledPrivate != 0 ? _soakSettledPrivate : _soakBaselinePrivate);
        bool pass = _soakSettledPrivate != 0
                 && Math.Abs(drift) < BudgetGate.SoakDriftBudgetBytes
                 && GC.CollectionCount(0) <= 8;

        Console.WriteLine();
        Console.WriteLine($"  soak — {hours:F2} h");
        Console.WriteLine("  " + new string('-', 58));
        Console.WriteLine($"  {"settled baseline",-24} "
            + $"{_soakSettledPrivate / (1024.0 * 1024),10:F1} MB   (at {SoakSettleSeconds / 60:F0} min)");
        Console.WriteLine($"  {"private bytes now",-24} {priv / (1024.0 * 1024),10:F1} MB   "
            + $"budget {BudgetGate.PrivateBytesBudget / (1024 * 1024)}");
        Console.WriteLine($"  {"drift since settling",-24} {drift / (1024.0 * 1024),10:F1} MB   "
            + $"budget {BudgetGate.SoakDriftBudgetBytes / (1024 * 1024)}");
        Console.WriteLine($"  {"handles",-24} {_self.HandleCount,10}");
        Console.WriteLine($"  {"Gen0 / Gen2",-24} {GC.CollectionCount(0),5} "
            + $"{GC.CollectionCount(2),4}");
        Console.WriteLine("  " + new string('-', 58));
        Console.WriteLine(pass ? "  SOAK PASS" : "  SOAK FAIL");
        if (!pass) SoakFailed = true;
    }

    /// <summary>Set when the soak's drift verdict failed, so Program can exit nonzero.</summary>
    public bool SoakFailed { get; private set; }

    /// <summary>
    /// How long the GL driver is allowed to finish allocating before drift is graded.
    /// Measured: flat from about 15 minutes on.
    /// </summary>
    private const double SoakSettleSeconds = 1200;

    private long _soakSettledPrivate;

    private Silk.NET.GLFW.Glfw? _glfw;

    /// <summary>
    /// Block until a window message arrives, or until <paramref name="timeout"/> seconds.
    ///
    /// Silk.NET.Windowing does not surface a blocking wait — IView.DoEvents is
    /// glfwPollEvents — so this goes to the GLFW binding underneath it. Guarded: if the
    /// binding cannot be reached we fall back to a coarse sleep, which is worse but still
    /// not a spin, and never a crash on a platform where the assumption does not hold.
    /// </summary>
    private void WaitForEvents(double timeout)
    {
        try
        {
            _glfw ??= Silk.NET.GLFW.Glfw.GetApi();
            _glfw.WaitEventsTimeout(timeout);
        }
        catch (Exception)
        {
            Thread.Sleep((int)(timeout * 1000));
            _window.DoEvents();
        }
    }

    private void OnTrayCommand(TrayIcon.Command cmd, int arg)
    {
        switch (cmd)
        {
            case TrayIcon.Command.ToggleVisible: ToggleVisible(); break;
            case TrayIcon.Command.TogglePause: _paused = !_paused; break;
            case TrayIcon.Command.Fullscreen: ToggleFullscreen(); break;
            case TrayIcon.Command.Quit: _window.Close(); break;
            case TrayIcon.Command.SelectPreset:
                // An explicit pick from the menu overrides the lock: the lock exists to stop
                // AUTOMATIC changes, and refusing a direct instruction would just look broken.
                SelectPreset(arg);
                AnnouncePreset();
                break;
        }
    }

    // ------------------------------------------------------------ lifecycle

    /// <summary>
    /// Bring up everything that is about the DESK rather than about the picture. Called at
    /// the end of OnLoad, and only when this is a real interactive run — a dump or a parity
    /// run must not register global hotkeys or plant a tray icon.
    /// </summary>
    private void StartDeskLayer()
    {
        _hwnd = NativeHandle();

        if (_cfg.Borderless) WindowChrome.SetBorderless(_hwnd, true);
        if (_cfg.AlwaysOnTop) WindowChrome.SetAlwaysOnTop(_hwnd, true);
        PlaceWindow();

        _intensity = _cfg.MasterIntensity;
        _governor.Auto = _cfg.GovernorAuto;
        _governor.SetTargetFps(_cfg.TargetFps);
        _governor.LevelChanged += OnGovernorLevel;

        _gpuTimer = new GpuTimer(_gl);
        Console.WriteLine($"[perf] frame timing: {(_gpuTimer.Available ? "GL timer queries" : "wall clock")}"
            + $", budget {_governor.BudgetMs:0.00} ms at {_cfg.TargetFps:0} fps"
            + $", governor {(_governor.Auto ? "auto" : "pinned")}");

        if (_cfg.Hotkeys)
        {
            _hotkeys = new HotKeys();
            _hotkeys.Start();
            Console.WriteLine($"[hotkeys] {HotKeys.Describe()}");
            Console.WriteLine("[hotkeys] unprefixed F N L D S [ ] Q work when the window has focus");
        }

        if (_cfg.TrayIcon)
        {
            _tray = new TrayIcon(
                () => _presets.Ids(),
                () => _presetIndex,
                () => _paused,
                () => _visible);
            _tray.Start();
            _tray.SetTooltip($"Ripple — {_preset.Id}");
        }

        WatchConfig();

        if (_cfg.FullscreenMonitor >= 0) ToggleFullscreen(_cfg.FullscreenMonitor);
    }

    private void StopDeskLayer()
    {
        SaveGeometryIfWindowed();
        _cfgWatcher?.Dispose();
        _cfgWatcher = null;
        _hotkeys?.Dispose();
        _hotkeys = null;
        _tray?.Dispose();
        _tray = null;
        _gpuTimer?.Dispose();
        _gpuTimer = null;
    }

    /// <summary>
    /// Silk.NET exposes the platform handle as a union; on Windows the Win32 member carries
    /// the HWND. Guarded because a null handle would turn every chrome call into a silent
    /// no-op against window 0, which looks like the interop being broken.
    /// </summary>
    private IntPtr NativeHandle()
    {
        try
        {
            var h = _window.Native?.Win32;
            if (h.HasValue) return h.Value.Hwnd;
            Console.Error.WriteLine("[window] no Win32 handle — chrome and hotkeys will be limited");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[window] native handle unavailable: {ex.Message}");
        }
        return IntPtr.Zero;
    }

    // --------------------------------------------------------------- window

    private void PlaceWindow()
    {
        if (_hwnd == IntPtr.Zero) return;

        int w = _cfg.Width, h = _cfg.Height;
        if (_cfg.X is int x && _cfg.Y is int y)
        {
            // Remembered geometry, but validated: a monitor that has gone away would leave
            // the window off-screen and unreachable, which on a borderless window with no
            // taskbar button means unrecoverable without editing the config by hand.
            if (OnSomeMonitor(x, y, w, h)) { WindowChrome.Move(_hwnd, x, y, w, h); return; }
            Console.WriteLine($"[window] saved position {x},{y} is off-screen now — re-cornering");
        }

        var m = WindowChrome.CurrentMonitor(_hwnd);
        var (cx, cy) = WindowChrome.CornerPosition(_cfg.Corner, _cfg.CornerMargin, w, h, m);
        WindowChrome.Move(_hwnd, cx, cy, w, h);
    }

    /// <summary>Is at least a reasonable chunk of this rectangle on some monitor?</summary>
    private static bool OnSomeMonitor(int x, int y, int w, int h)
    {
        foreach (var m in WindowChrome.Monitors())
        {
            int ox = Math.Max(0, Math.Min(x + w, m.X + m.Width) - Math.Max(x, m.X));
            int oy = Math.Max(0, Math.Min(y + h, m.Y + m.Height) - Math.Max(y, m.Y));
            // A quarter of the window visible is enough to grab and move.
            if (ox * oy > w * h / 4) return true;
        }
        return false;
    }

    private void SaveGeometryIfWindowed()
    {
        if (_fullscreen || _hwnd == IntPtr.Zero) return;
        var (x, y, w, h) = WindowChrome.GetBounds(_hwnd);
        if (w > 0 && h > 0) _cfg.SaveGeometry(x, y, w, h);
    }

    private void ToggleFullscreen(int monitor = -1)
    {
        if (_hwnd == IntPtr.Zero) return;

        if (!_fullscreen)
        {
            var (x, y, w, h) = WindowChrome.GetBounds(_hwnd);
            (_windowedX, _windowedY, _windowedW, _windowedH) = (x, y, w, h);

            int target = monitor >= 0
                ? monitor
                : IndexOfCurrentMonitor();
            _fullscreen = WindowChrome.Fullscreen(_hwnd, target);
            if (_fullscreen) Console.WriteLine($"[window] fullscreen on monitor {target}");
            return;
        }

        _fullscreen = false;
        WindowChrome.SetBorderless(_hwnd, _cfg.Borderless);
        WindowChrome.SetAlwaysOnTop(_hwnd, _cfg.AlwaysOnTop);
        if (_windowedW > 0)
            WindowChrome.Move(_hwnd, _windowedX, _windowedY, _windowedW, _windowedH);
        Console.WriteLine("[window] windowed");
    }

    private int IndexOfCurrentMonitor()
    {
        var cur = WindowChrome.CurrentMonitor(_hwnd);
        var all = WindowChrome.Monitors();
        for (int i = 0; i < all.Count; i++)
            if (all[i].X == cur.X && all[i].Y == cur.Y) return i;
        return 0;
    }

    private void ToggleVisible()
    {
        _visible = !_visible;
        if (_visible)
        {
            WindowChrome.Show(_hwnd);
            // Re-assert topmost: a window that was hidden loses its place in the Z order,
            // and coming back underneath everything looks like the toggle not working.
            if (_cfg.AlwaysOnTop || _fullscreen) WindowChrome.SetAlwaysOnTop(_hwnd, true);
        }
        else
        {
            SaveGeometryIfWindowed();
            WindowChrome.Hide(_hwnd);
        }
        _tray?.SetTooltip($"Ripple — {(_visible ? _preset.Id : "hidden")}");
    }

    // --------------------------------------------------------------- config

    private void WatchConfig()
    {
        try
        {
            string dir = RippleConfig.DefaultDir;
            Directory.CreateDirectory(dir);
            _cfgWatcher = new FileSystemWatcher(dir, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                             | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            // Flag only. The handler runs on a watcher thread and the reload touches GL
            // state, so the work happens on the render thread in PollConfig.
            _cfgWatcher.Changed += (_, _) => _cfgDirty = true;
            _cfgWatcher.Created += (_, _) => _cfgDirty = true;
            _cfgWatcher.Renamed += (_, _) => _cfgDirty = true;
            Console.WriteLine($"[watch] {RippleConfig.DefaultPath} hot-reloads on save");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] cannot watch for changes: {ex.Message}");
        }
    }

    private void PollConfig()
    {
        if (!_cfgDirty) return;
        _cfgDirty = false;

        // Editors write in more than one operation — truncate, then write — so the first
        // notification can arrive against a half-written file. A failed parse keeps the
        // previous values, and the next notification picks up the finished file.
        if (!_cfg.Reload()) return;

        _governor.Auto = _cfg.GovernorAuto;
        _governor.SetTargetFps(_cfg.TargetFps);
        _intensity = Math.Clamp(_cfg.MasterIntensity, 0.05, 1.0);

        if (!_fullscreen)
        {
            WindowChrome.SetBorderless(_hwnd, _cfg.Borderless);
            WindowChrome.SetAlwaysOnTop(_hwnd, _cfg.AlwaysOnTop);
        }

        if (Math.Abs(_cfg.RenderScale - _renderScale) > 1e-6 && !_governor.Auto)
        {
            _renderScale = _cfg.RenderScale;
            _chain.Resize(_window.FramebufferSize.X, _window.FramebufferSize.Y, _renderScale);
        }

        Console.WriteLine("[config] reloaded");
    }

    // ------------------------------------------------------------- governor

    private void OnGovernorLevel(int level, string label)
    {
        // Render scale is the governor's largest dial and the only one that needs an
        // allocation, so it is applied here rather than per-frame in DrawState.
        if (!_governor.Auto) return;
        _renderScale = _governor.RenderScale;
        _chain.Resize(_window.FramebufferSize.X, _window.FramebufferSize.Y, _renderScale);
        Console.WriteLine($"[governor] tier {level} ({label}) — scale {_renderScale:0.00}, "
            + $"measured {_governor.MeasuredMs:0.00} ms vs budget {_governor.BudgetMs:0.00} ms");
    }

    // --------------------------------------------------------------- input

    /// <summary>
    /// Dispatch a hotkey or its window-local equivalent. One switch for both paths, so a
    /// binding cannot mean two different things depending on how it arrived.
    /// </summary>
    private void DoAction(HotkeyAction a)
    {
        switch (a)
        {
            case HotkeyAction.ToggleVisible: ToggleVisible(); break;
            case HotkeyAction.Fullscreen: ToggleFullscreen(); break;

            case HotkeyAction.NextPreset:
                if (_presetLocked) { Console.WriteLine("[preset] locked"); break; }
                SelectPreset((_presetIndex + 1) % _presets.Library.Count);
                AnnouncePreset();
                break;

            case HotkeyAction.PrevPreset:
                if (_presetLocked) { Console.WriteLine("[preset] locked"); break; }
                SelectPreset((_presetIndex - 1 + _presets.Library.Count) % _presets.Library.Count);
                AnnouncePreset();
                break;

            case HotkeyAction.LockPreset:
                _presetLocked = !_presetLocked;
                Console.WriteLine($"[preset] {(_presetLocked ? "locked" : "unlocked")} on {_preset.Id}");
                break;

            case HotkeyAction.ToggleHud:
                _hudOn = !_hudOn;
                if (!_hudOn) Console.WriteLine("[hud] off");
                break;

            case HotkeyAction.Screenshot: Screenshot(); break;

            case HotkeyAction.IntensityDown:
                _intensity = Math.Max(0.05, _intensity - 0.1);
                Console.WriteLine($"[intensity] {_intensity:0.00}");
                break;

            case HotkeyAction.IntensityUp:
                // Ceiling of 1.0, not "whatever the user asked for". Master intensity is
                // inside §12's envelope: it can only ever take brightness away.
                _intensity = Math.Min(1.0, _intensity + 0.1);
                Console.WriteLine($"[intensity] {_intensity:0.00}");
                break;

            case HotkeyAction.TogglePause: _paused = !_paused; break;
            case HotkeyAction.Quit: _window.Close(); break;
        }
    }

    private void AnnouncePreset()
    {
        Console.WriteLine($"[preset] {_preset.Id} — {_preset.Name}");
        _tray?.SetTooltip($"Ripple — {_preset.Id}");
    }

    private void Screenshot()
    {
        try
        {
            string dir = _cfg.ResolvedScreenshotDir;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir,
                $"ripple-{DateTime.Now:yyyyMMdd-HHmmss}-{_preset.Id}.png");
            _chain.SavePng(_gl, path);
            Console.WriteLine($"[screenshot] {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screenshot] failed: {ex.Message}");
        }
    }

    /// <summary>
    /// §11's auto-advance, driven off the novelty feature. Off by default — see the config
    /// note. Rate-limited so a busy passage cannot walk the whole library in a minute.
    /// </summary>
    private void MaybeAutoAdvance(ReadOnlySpan<float> f)
    {
        if (!_cfg.AutoAdvanceOnNovelty || _presetLocked) return;
        if (f[FF.Novelty] < _cfg.NoveltyThreshold) return;
        if (_tSec - _lastNoveltyAdvance < _cfg.MinSecondsBetweenAdvances) return;

        _lastNoveltyAdvance = _tSec;
        SelectPreset((_presetIndex + 1) % _presets.Library.Count);
        Console.WriteLine($"[preset] novelty {f[FF.Novelty]:0.00} -> {_preset.Id}");
        _tray?.SetTooltip($"Ripple — {_preset.Id}");
    }

    // ------------------------------------------------------------------ HUD

    private void MaybeHud(double dt)
    {
        if (!_hudOn) return;
        _hudTimer += dt;
        if (_hudTimer < 1.0) return;
        _hudTimer = 0;

        string timing = _gpuTimer?.Available == true ? "gpu" : "wall";
        Console.WriteLine($"[hud] {_preset.Id} · {_chain.ResolvedFamily} · "
            + $"{_chain.Targets.Pw}x{_chain.Targets.Ph} · tier {_governor.Level} · "
            + $"{_governor.MeasuredMs:0.00} ms {timing} / {_governor.BudgetMs:0.00} budget · "
            + $"{_idle.Current} dim {_idle.Dim:0.00} · intensity {_intensity:0.00} · "
            + $"limiter {_host.SafeScale:0.000} flash {_host.FlashRate:0.0} Hz · "
            + $"ws {Environment.WorkingSet / (1024 * 1024)} MB");
    }
}
