using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Ripple.Ui;

/// <summary>What a hotkey asked for. The host decides what each one means.</summary>
public enum HotkeyAction
{
    ToggleVisible, Fullscreen, NextPreset, PrevPreset, LockPreset,
    ToggleHud, Screenshot, IntensityDown, IntensityUp, Quit, TogglePause,
}

/// <summary>
/// System-wide hotkeys via <c>RegisterHotKey</c>.
///
/// A DESIGN DECISION WORTH KNOWING ABOUT. The plan lists the bindings as
/// "Ctrl+Alt+V show/hide · F fullscreen · N next preset · L lock · D HUD · S screenshot ·
/// [ / ] intensity · Q quit", all under "global". Registering the BARE letters globally is
/// not implementable in any useful sense: RegisterHotKey takes the key away from every other
/// process on the desktop, so a global bare `F` means nobody can type the letter F in any
/// application while Ripple is running. It would also collide immediately — `S` alone is
/// claimed by other tools all over a normal Windows install.
///
/// So the bindings are split by where they need to work:
///
///   GLOBAL, with a Ctrl+Alt prefix. Ctrl+Alt+V has to be global — it is what brings back a
///   hidden window, and a hidden window cannot receive a keystroke. The rest are mirrored to
///   Ctrl+Alt+&lt;letter&gt; so they also work while Ripple is not focused.
///
///   WINDOW-LOCAL, as bare letters, handled by the normal keyboard input path. When the
///   window has focus, F / N / L / D / S / [ / ] / Q behave exactly as the plan lists them.
///
/// Same letters, same meanings, and nothing is stolen from the rest of the desktop.
///
/// The message pump runs on its OWN thread with a message-only window, rather than on the
/// render thread. GLFW's PeekMessage loop retrieves thread-queue messages and dispatches
/// them, but WM_HOTKEY posted to a null HWND has no window to dispatch to and is silently
/// dropped — so hotkeys registered against the render thread appear to do nothing. Owning a
/// real HWND on a thread we control removes the ambiguity entirely.
/// </summary>
public sealed partial class HotKeys : IDisposable
{
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModNoRepeat = 0x4000;
    private const uint WmHotkey = 0x0312, WmClose = 0x0010, WmDestroy = 0x0002;
    private const uint WmQuit = 0x0012;
    private static readonly IntPtr HwndMessage = new(-3);

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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

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

    /// <summary>
    /// The bindings, as data. Order is the registration order and the id order, so a
    /// diagnostic naming id 3 names the same binding across runs.
    /// </summary>
    private static readonly (HotkeyAction Action, uint Vk, string Name)[] Bindings =
    [
        (HotkeyAction.ToggleVisible, 0x56, "Ctrl+Alt+V"),   // V
        (HotkeyAction.Fullscreen, 0x46, "Ctrl+Alt+F"),      // F
        (HotkeyAction.NextPreset, 0x4E, "Ctrl+Alt+N"),      // N
        (HotkeyAction.LockPreset, 0x4C, "Ctrl+Alt+L"),      // L
        (HotkeyAction.ToggleHud, 0x44, "Ctrl+Alt+D"),       // D
        (HotkeyAction.Screenshot, 0x53, "Ctrl+Alt+S"),      // S
        (HotkeyAction.IntensityDown, 0xDB, "Ctrl+Alt+["),   // VK_OEM_4
        (HotkeyAction.IntensityUp, 0xDD, "Ctrl+Alt+]"),     // VK_OEM_6
        (HotkeyAction.Quit, 0x51, "Ctrl+Alt+Q"),            // Q
    ];

    private readonly ConcurrentQueue<HotkeyAction> _pending = new();
    private readonly WndProcDelegate _wndProc;      // held so the GC cannot collect it
    private Thread? _thread;
    private IntPtr _hwnd;
    private volatile bool _running;

    /// <summary>Bindings that could not be claimed, usually because something else has them.</summary>
    public List<string> Failed { get; } = [];

    public HotKeys()
    {
        _wndProc = (h, m, w, l) => m == WmClose || m == WmDestroy
            ? IntPtr.Zero
            : DefWindowProcW(h, m, w, l);
    }

    /// <summary>
    /// Start the pump. Never throws: no hotkeys is a degraded app, not a dead one, and on
    /// some managed desktops RegisterHotKey is simply denied.
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;

        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => Pump(ready))
        {
            IsBackground = true,
            Name = "ripple-hotkeys",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // Bounded wait: if the pump cannot come up we carry on without hotkeys rather than
        // hanging the launch on it.
        if (!ready.Wait(TimeSpan.FromSeconds(2)))
            Console.Error.WriteLine("[hotkeys] pump did not start in time — continuing without");
    }

    private void Pump(ManualResetEventSlim ready)
    {
        try
        {
            string cls = "RippleHotkeyWindow";
            var wc = new WndClassEx
            {
                CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                Instance = GetModuleHandle(IntPtr.Zero),
                ClassName = Marshal.StringToHGlobalUni(cls),
            };

            RegisterClassEx(ref wc);        // a duplicate class is fine; CreateWindowEx tells us
            Marshal.FreeHGlobal(wc.ClassName);

            // HWND_MESSAGE: a message-only window. No pixels, no taskbar presence, no
            // painting — it exists purely to own a message queue.
            _hwnd = CreateWindowEx(0, cls, "Ripple", 0, 0, 0, 0, 0,
                HwndMessage, IntPtr.Zero, GetModuleHandle(IntPtr.Zero), IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Console.Error.WriteLine($"[hotkeys] no message window "
                    + $"(win32 {Marshal.GetLastWin32Error()}) — continuing without hotkeys");
                ready.Set();
                return;
            }

            for (int i = 0; i < Bindings.Length; i++)
            {
                var (_, vk, name) = Bindings[i];
                // MOD_NOREPEAT: without it, holding the combination fires continuously, and
                // "next preset" held for a second would run the whole library.
                if (!RegisterHotKey(_hwnd, i, ModControl | ModAlt | ModNoRepeat, vk))
                    Failed.Add(name);
            }

            if (Failed.Count > 0)
                Console.Error.WriteLine($"[hotkeys] already taken: {string.Join(", ", Failed)}");

            ready.Set();

            while (_running && GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Message == WmHotkey)
                {
                    int id = msg.WParam.ToInt32();
                    if (id >= 0 && id < Bindings.Length) _pending.Enqueue(Bindings[id].Action);
                }
                else
                {
                    DispatchMessageW(ref msg);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[hotkeys] pump failed: {ex.Message}");
            ready.Set();
        }
    }

    /// <summary>
    /// Drain what has arrived since the last call. Called from the render thread, so the
    /// handlers run where the GL context is — a hotkey that resized a render target from the
    /// pump thread would be making GL calls with no context bound.
    /// </summary>
    public void Drain(Action<HotkeyAction> handle)
    {
        while (_pending.TryDequeue(out var a))
        {
            try { handle(a); }
            catch (Exception ex) { Console.Error.WriteLine($"[hotkeys] {a} failed: {ex.Message}"); }
        }
    }

    public static string Describe()
        => string.Join(" · ", Bindings.Select(b => $"{b.Name} {b.Action}"));

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                for (int i = 0; i < Bindings.Length; i++) UnregisterHotKey(_hwnd, i);
                // Wake GetMessage so the loop can notice _running went false.
                PostMessageW(_hwnd, WmQuit, IntPtr.Zero, IntPtr.Zero);
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
        catch (Exception)
        {
            // Teardown; the process is going away regardless.
        }
        _thread?.Join(TimeSpan.FromSeconds(1));
    }
}
