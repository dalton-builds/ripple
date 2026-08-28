using System.Runtime.InteropServices;

namespace Ripple.Ui;

/// <summary>
/// Borderless, always-on-top and fullscreen-on-a-chosen-monitor, via Win32.
///
/// Silk.NET's windowing sits on GLFW, and GLFW deliberately does not expose these: it has
/// no always-on-top toggle after creation, no click-through, and its "fullscreen" takes over
/// a monitor exclusively rather than becoming a borderless window the size of one. All three
/// are the difference between a demo and something that can live on a desk, so they are done
/// against the HWND directly.
///
/// Every P/Invoke here is checked and every failure is non-fatal. A window that could not be
/// made topmost is a window that is not topmost — it is not a reason to take the app down,
/// and on a locked-down desktop some of these calls do fail.
/// </summary>
public static partial class WindowChrome
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsThickFrame = 0x00040000;

    private const uint WsExTopmost = 0x00000008;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExToolWindow = 0x00000080;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);

    private const uint LwaAlpha = 0x2;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SetWindowLongW(IntPtr hWnd, int nIndex, uint dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey,
        byte bAlpha, uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        MonitorEnumProc lpfnEnum, IntPtr dwData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    private const int SwHide = 0, SwShowNoActivate = 4, SwRestore = 9;

    /// <summary>A monitor's bounds, in virtual-desktop coordinates.</summary>
    public readonly record struct Monitor(int X, int Y, int Width, int Height, bool Primary)
    {
        public override string ToString()
            => $"{Width}x{Height} at {X},{Y}{(Primary ? " (primary)" : "")}";
    }

    /// <summary>
    /// Every monitor, in EnumDisplayMonitors order. That order is stable for a given display
    /// arrangement, which is what makes "fullscreen on monitor 1" mean the same thing twice.
    /// </summary>
    public static List<Monitor> Monitors()
    {
        var found = new List<Monitor>();
        try
        {
            // The delegate must stay alive for the duration of the call; it does, because
            // EnumDisplayMonitors is synchronous and the local outlives it.
            MonitorEnumProc cb = (h, _, _, _) =>
            {
                var mi = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(h, ref mi))
                {
                    found.Add(new Monitor(
                        mi.Monitor.Left, mi.Monitor.Top,
                        mi.Monitor.Right - mi.Monitor.Left,
                        mi.Monitor.Bottom - mi.Monitor.Top,
                        (mi.Flags & 1) != 0));
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[window] could not enumerate monitors: {ex.Message}");
        }
        return found;
    }

    /// <summary>The monitor the window is currently mostly on.</summary>
    public static Monitor CurrentMonitor(IntPtr hwnd)
    {
        try
        {
            var h = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var mi = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(h, ref mi))
            {
                return new Monitor(mi.Monitor.Left, mi.Monitor.Top,
                    mi.Monitor.Right - mi.Monitor.Left,
                    mi.Monitor.Bottom - mi.Monitor.Top, (mi.Flags & 1) != 0);
            }
        }
        catch (Exception) { /* fall through to the primary guess */ }

        var all = Monitors();
        return all.Count > 0 ? all[0] : new Monitor(0, 0, 1920, 1080, true);
    }

    /// <summary>Strip the caption and the resize frame, keeping the window in place.</summary>
    public static void SetBorderless(IntPtr hwnd, bool borderless)
    {
        try
        {
            uint style = GetWindowLongW(hwnd, GwlStyle);
            style = borderless
                ? (style & ~(WsCaption | WsThickFrame | WsOverlappedWindow)) | WsPopup | WsVisible
                : (style & ~WsPopup) | WsOverlappedWindow | WsVisible;
            SetWindowLongW(hwnd, GwlStyle, style);

            // SWP_FRAMECHANGED is mandatory: without it the non-client area is not
            // recalculated and the old caption stays painted until something else forces it.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[window] borderless failed: {ex.Message}");
        }
    }

    public static void SetAlwaysOnTop(IntPtr hwnd, bool onTop)
    {
        try
        {
            SetWindowPos(hwnd, onTop ? HwndTopmost : HwndNoTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[window] always-on-top failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Click-through: the window is visible but takes no mouse input, so it can sit over a
    /// working desktop without stealing clicks. WS_EX_LAYERED is required alongside
    /// WS_EX_TRANSPARENT — transparent alone is honoured inconsistently, and layered is what
    /// makes the hit-testing exclusion actually apply.
    /// </summary>
    public static void SetClickThrough(IntPtr hwnd, bool through)
    {
        try
        {
            uint ex = GetWindowLongW(hwnd, GwlExStyle);
            ex = through ? ex | WsExTransparent | WsExLayered
                         : ex & ~WsExTransparent;
            SetWindowLongW(hwnd, GwlExStyle, ex);
            if (through) SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
        }
        catch (Exception ex2)
        {
            Console.Error.WriteLine($"[window] click-through failed: {ex2.Message}");
        }
    }

    /// <summary>Keep it out of the alt-tab list, which is what you want of a wallpaper.</summary>
    public static void SetToolWindow(IntPtr hwnd, bool tool)
    {
        try
        {
            uint ex = GetWindowLongW(hwnd, GwlExStyle);
            ex = tool ? ex | WsExToolWindow : ex & ~WsExToolWindow;
            SetWindowLongW(hwnd, GwlExStyle, ex);
        }
        catch (Exception ex2)
        {
            Console.Error.WriteLine($"[window] tool-window failed: {ex2.Message}");
        }
    }

    public static void Move(IntPtr hwnd, int x, int y, int w, int h)
    {
        try
        {
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SwpNoZOrder | SwpNoActivate);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[window] move failed: {ex.Message}");
        }
    }

    public static (int X, int Y, int W, int H) GetBounds(IntPtr hwnd)
    {
        try
        {
            if (GetWindowRect(hwnd, out var r))
                return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
        catch (Exception) { /* fall through */ }
        return (0, 0, 0, 0);
    }

    /// <summary>
    /// Borderless-fullscreen on a chosen monitor: a popup window exactly covering it.
    ///
    /// Deliberately NOT exclusive fullscreen. Exclusive mode changes the display mode, takes
    /// a mode-set hit on every alt-tab, and can black every other monitor while it switches —
    /// all of which is wrong for something you glance at and then go back to work.
    /// </summary>
    public static bool Fullscreen(IntPtr hwnd, int monitorIndex)
    {
        var all = Monitors();
        if (all.Count == 0) return false;
        var m = all[Math.Clamp(monitorIndex, 0, all.Count - 1)];

        SetBorderless(hwnd, true);
        try
        {
            SetWindowPos(hwnd, HwndTopmost, m.X, m.Y, m.Width, m.Height,
                SwpFrameChanged | SwpShowWindow);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[window] fullscreen failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Minimized. Occluded-but-not-minimized is a separate, more expensive question.</summary>
    public static bool IsMinimized(IntPtr hwnd)
    {
        try { return IsIconic(hwnd); }
        catch (Exception) { return false; }
    }

    /// <summary>Hide without destroying — the Ctrl+Alt+V "dismiss" half.</summary>
    public static void Hide(IntPtr hwnd)
    {
        try { ShowWindow(hwnd, SwHide); }
        catch (Exception ex) { Console.Error.WriteLine($"[window] hide failed: {ex.Message}"); }
    }

    /// <summary>
    /// Show again. SW_SHOWNOACTIVATE rather than SW_SHOW: restoring the visualizer should not
    /// take focus away from whatever you were typing in, which is the whole point of a
    /// global hotkey.
    /// </summary>
    public static void Show(IntPtr hwnd, bool focus = false)
    {
        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
            else ShowWindow(hwnd, SwShowNoActivate);
            if (focus) SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[window] show failed: {ex.Message}");
        }
    }

    /// <summary>Place a window into a screen corner, respecting the work area.</summary>
    public static (int X, int Y) CornerPosition(string corner, int margin, int w, int h,
                                                Monitor m)
    {
        int left = m.X + margin;
        int top = m.Y + margin;
        int right = m.X + m.Width - w - margin;
        int bottom = m.Y + m.Height - h - margin;

        return corner.ToLowerInvariant() switch
        {
            "top-left" => (left, top),
            "top-right" => (right, top),
            "bottom-left" => (left, bottom),
            "center" => (m.X + (m.Width - w) / 2, m.Y + (m.Height - h) / 2),
            _ => (right, bottom),      // bottom-right, the default
        };
    }
}
