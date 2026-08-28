using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Ripple.Ui;

/// <summary>
/// A notification-area icon with a right-click menu: show/hide, pause, preset picker, quit.
///
/// This is the "dismissible and restorable" half of the prototype gate that does not depend
/// on remembering a key combination. A borderless always-on-top window with no caption has
/// no close button and no taskbar button, so without a tray icon the only way to get rid of
/// it is a hotkey — and the moment that hotkey collides with something else, the app is
/// unkillable by ordinary means.
///
/// Runs its own message-only window on its own thread, for the same reason
/// <see cref="HotKeys"/> does: the tray callback is a window message, and GLFW's pump is not
/// ours to extend. Menu commands are queued and drained on the render thread.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04;
    private const uint WmApp = 0x8000;
    private const uint WmTrayCallback = WmApp + 1;
    private const uint WmRButtonUp = 0x0205, WmLButtonDblClk = 0x0203, WmCommand = 0x0111;
    private const uint WmQuit = 0x0012;
    private const uint MfString = 0x0000, MfSeparator = 0x0800, MfPopup = 0x0010;
    private const uint MfChecked = 0x0008, MfUnchecked = 0x0000;
    private const uint TpmRightButton = 0x0002, TpmReturnCmd = 0x0100;
    private static readonly IntPtr HwndMessage = new(-3);

    /// <summary>Command ids. Presets start at <see cref="PresetBase"/> plus their index.</summary>
    private const int CmdShowHide = 1, CmdPause = 2, CmdQuit = 3, CmdFullscreen = 4;
    private const int PresetBase = 1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint CbSize;
        public IntPtr Hwnd;
        public uint Id, Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int PtX, PtY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint CbSize, Style;
        public IntPtr WndProc;
        public int CbClsExtra, CbWndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public IntPtr MenuName, ClassName, IconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    // Classic DllImport rather than LibraryImport: NOTIFYICONDATA carries fixed-length
    // ByValTStr buffers, and the P/Invoke source generator rejects those (SYSLIB1051). The
    // runtime marshaller handles them, so this one entry point stays on the old mechanism.
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint msg, ref NotifyIconData data);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static partial IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(IntPtr menu);

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(IntPtr menu, uint flags, IntPtr idNewItem, string? item);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int TrackPopupMenu(IntPtr menu, uint flags, int x, int y,
        int reserved, IntPtr hWnd, IntPtr rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point p);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetMessageW(out Msg lpMsg, IntPtr hWnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DispatchMessageW(ref Msg lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassEx(ref WndClassEx c);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowEx(uint exStyle, string className,
        string windowName, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
    private static partial IntPtr GetModuleHandle(IntPtr name);

    /// <summary>What the menu asked for. Preset carries the library index.</summary>
    public enum Command { ToggleVisible, TogglePause, Fullscreen, Quit, SelectPreset }

    private readonly ConcurrentQueue<(Command Cmd, int Arg)> _pending = new();
    private readonly WndProcDelegate _wndProc;
    private readonly Func<IReadOnlyList<string>> _presetNames;
    private readonly Func<int> _currentPreset;
    private readonly Func<bool> _isPaused;
    private readonly Func<bool> _isVisible;

    private Thread? _thread;
    private IntPtr _hwnd;
    private NotifyIconData _data;
    private volatile bool _running;
    private volatile bool _added;

    /// <summary>
    /// The menu is built on demand from these callbacks rather than from a snapshot, so it
    /// shows the CURRENT preset and pause state every time it opens. They are read from the
    /// tray thread while the render thread may be writing, which is why each is a simple
    /// value read — enough for a menu that is about to be replaced by the user's choice.
    /// </summary>
    public TrayIcon(Func<IReadOnlyList<string>> presetNames, Func<int> currentPreset,
                    Func<bool> isPaused, Func<bool> isVisible)
    {
        _presetNames = presetNames;
        _currentPreset = currentPreset;
        _isPaused = isPaused;
        _isVisible = isVisible;
        _wndProc = WndProc;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => Pump(ready))
        {
            IsBackground = true,
            Name = "ripple-tray",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(2)))
            Console.Error.WriteLine("[tray] did not start in time — continuing without");
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WmTrayCallback)
        {
            uint ev = (uint)(l.ToInt64() & 0xFFFF);
            if (ev == WmRButtonUp) ShowMenu(hWnd);
            else if (ev == WmLButtonDblClk) _pending.Enqueue((Command.ToggleVisible, 0));
            return IntPtr.Zero;
        }
        if (msg == WmCommand)
        {
            Dispatch(w.ToInt32() & 0xFFFF);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, w, l);
    }

    private void Dispatch(int id)
    {
        if (id >= PresetBase) { _pending.Enqueue((Command.SelectPreset, id - PresetBase)); return; }
        switch (id)
        {
            case CmdShowHide: _pending.Enqueue((Command.ToggleVisible, 0)); break;
            case CmdPause: _pending.Enqueue((Command.TogglePause, 0)); break;
            case CmdFullscreen: _pending.Enqueue((Command.Fullscreen, 0)); break;
            case CmdQuit: _pending.Enqueue((Command.Quit, 0)); break;
        }
    }

    private void ShowMenu(IntPtr hWnd)
    {
        IntPtr menu = IntPtr.Zero, presets = IntPtr.Zero;
        try
        {
            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;

            AppendMenu(menu, MfString, new IntPtr(CmdShowHide), _isVisible() ? "Hide" : "Show");
            AppendMenu(menu, MfString | (_isPaused() ? MfChecked : MfUnchecked),
                       new IntPtr(CmdPause), "Pause");
            AppendMenu(menu, MfString, new IntPtr(CmdFullscreen), "Fullscreen");
            AppendMenu(menu, MfSeparator, IntPtr.Zero, null);

            var names = _presetNames();
            int current = _currentPreset();
            if (names.Count > 0)
            {
                presets = CreatePopupMenu();
                for (int i = 0; i < names.Count; i++)
                {
                    AppendMenu(presets, MfString | (i == current ? MfChecked : MfUnchecked),
                               new IntPtr(PresetBase + i), names[i]);
                }
                AppendMenu(menu, MfPopup | MfString, presets, "Preset");
                AppendMenu(menu, MfSeparator, IntPtr.Zero, null);
            }

            AppendMenu(menu, MfString, new IntPtr(CmdQuit), "Quit Ripple");

            // Required, and easy to miss: without foreground ownership the menu does not
            // dismiss when you click away from it and stays on screen indefinitely.
            SetForegroundWindow(hWnd);
            GetCursorPos(out var p);

            // TPM_RETURNCMD makes this synchronous — the id comes back rather than arriving
            // as a WM_COMMAND. Simpler to reason about, and it keeps the menu's lifetime
            // inside this try/finally.
            int picked = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd,
                                        p.X, p.Y, 0, hWnd, IntPtr.Zero);
            if (picked != 0) Dispatch(picked);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tray] menu failed: {ex.Message}");
        }
        finally
        {
            if (presets != IntPtr.Zero) DestroyMenu(presets);
            if (menu != IntPtr.Zero) DestroyMenu(menu);
        }
    }

    private void Pump(ManualResetEventSlim ready)
    {
        try
        {
            string cls = "RippleTrayWindow";
            var wc = new WndClassEx
            {
                CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                Instance = GetModuleHandle(IntPtr.Zero),
                ClassName = Marshal.StringToHGlobalUni(cls),
            };
            RegisterClassEx(ref wc);
            Marshal.FreeHGlobal(wc.ClassName);

            _hwnd = CreateWindowEx(0, cls, "Ripple", 0, 0, 0, 0, 0,
                HwndMessage, IntPtr.Zero, GetModuleHandle(IntPtr.Zero), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Console.Error.WriteLine("[tray] no message window — continuing without a tray icon");
                ready.Set();
                return;
            }

            // IDI_APPLICATION (32512). The app has no .ico yet — the csproj conditions
            // ApplicationIcon on the file existing — and a generic icon that appears beats a
            // custom one that does not.
            _data = new NotifyIconData
            {
                CbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                Hwnd = _hwnd,
                Id = 1,
                Flags = NifMessage | NifIcon | NifTip,
                CallbackMessage = WmTrayCallback,
                Icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)),
                Tip = "Ripple",
                Info = "",
                InfoTitle = "",
            };

            _added = ShellNotifyIcon(NimAdd, ref _data);
            if (!_added)
                Console.Error.WriteLine("[tray] Shell_NotifyIcon refused the icon");

            ready.Set();

            while (_running && GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                DispatchMessageW(ref msg);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tray] pump failed: {ex.Message}");
            ready.Set();
        }
    }

    /// <summary>Update the tooltip — cheap enough to carry the current preset name.</summary>
    public void SetTooltip(string text)
    {
        if (!_added) return;
        try
        {
            _data.Tip = text.Length > 120 ? text[..120] : text;
            _data.Flags = NifTip;
            ShellNotifyIcon(NimModify, ref _data);
        }
        catch (Exception) { /* a stale tooltip is not worth reporting */ }
    }

    public void Drain(Action<Command, int> handle)
    {
        while (_pending.TryDequeue(out var c))
        {
            try { handle(c.Cmd, c.Arg); }
            catch (Exception ex) { Console.Error.WriteLine($"[tray] {c.Cmd} failed: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        try
        {
            if (_added) { ShellNotifyIcon(NimDelete, ref _data); _added = false; }
            if (_hwnd != IntPtr.Zero)
            {
                PostMessageW(_hwnd, WmQuit, IntPtr.Zero, IntPtr.Zero);
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
        catch (Exception)
        {
            // The icon is removed by the shell when the process dies anyway.
        }
        _thread?.Join(TimeSpan.FromSeconds(1));
    }
}
