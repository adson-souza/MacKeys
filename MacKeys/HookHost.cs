using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static MacKeys.Native;

namespace MacKeys;

/// <summary>
/// Runs the low-level keyboard and mouse hooks on their own high-priority thread with its own message loop.
/// Windows gives a hook only a short time to answer; on the UI thread, any busy moment (decoding an image,
/// building a thumbnail) made Windows skip MacKeys and deliver the key unprocessed. When that key was ⌘'s
/// release, the injected Ctrl stayed down and the whole keyboard behaved as if stuck.
///
/// The thread also runs a 200 ms timer (the watchdog) and any work handed to it with <see cref="Invoke"/>.
/// Nothing else may run here: Raw Input in particular lives on its own thread (see <see cref="RawKeyboard"/>).
/// </summary>
internal sealed class HookHost : IDisposable
{
    private readonly LowLevelHookProc _keyboard;
    private readonly LowLevelHookProc _mouse;
    private readonly Action _onTick;
    private readonly Action _warmUp;

    /// <summary>A physical key event forwarded by the Raw Input helper process (vk, isDown).</summary>
    public event Action<Keys, bool>? RawKey;

    /// <summary>The hook thread's message-only window: the Raw Input helper posts physical keys to it.</summary>
    public IntPtr Hwnd => _hwnd;

    public const uint WM_RAWKEY = 0x8002;
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly WndProc _wndProc;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _keyboardHook, _mouseHook;
    private Exception? _startError;

    /// <param name="warmUp">
    /// Runs on the hook thread before the hooks go in. The first key press would otherwise compile the whole
    /// keyboard path on the spot; if that takes longer than Windows' hook timeout, Windows silently removes
    /// the hook for good (seen in the self-test as a hook that sometimes never fires).
    /// </param>
    public HookHost(LowLevelHookProc keyboard, LowLevelHookProc mouse, Action onTick, Action warmUp)
    {
        _keyboard = keyboard;
        _mouse = mouse;
        _onTick = onTick;
        _warmUp = warmUp;
        _wndProc = WindowProc;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "MacKeys keyboard", Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait();
        if (_startError != null) throw _startError;
    }

    /// <summary>Runs an action on the hook thread (where all keyboard state lives).</summary>
    public void Invoke(Action action)
    {
        _pending.Enqueue(action);
        PostMessage(_hwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        try
        {
            CreateMessageWindow();
            _warmUp();
            InstallHooks();
            SetTimer(_hwnd, (IntPtr)1, 200, IntPtr.Zero);
        }
        catch (Exception e)
        {
            _startError = e;
            _ready.Set();
            return;
        }
        _ready.Set();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        RemoveHooks();
    }

    private void InstallHooks()
    {
        IntPtr module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboard, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouse, module, 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx falhou: {Marshal.GetLastWin32Error()}");
    }

    private void RemoveHooks()
    {
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }

    private void CreateMessageWindow()
    {
        var cls = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "MacKeysHookWindow",
        };
        RegisterClassEx(ref cls);
        _hwnd = CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx falhou: {Marshal.GetLastWin32Error()}");
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_TIMER:
                    _onTick();
                    return IntPtr.Zero;
                case WM_RUN:
                    while (_pending.TryDequeue(out var action)) action();
                    return IntPtr.Zero;
                case WM_RAWKEY:
                    RawKey?.Invoke((Keys)(int)wParam, lParam != IntPtr.Zero);
                    return IntPtr.Zero;
                case WM_CLOSE:
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception e)
        {
            DebugLog.Write($"Teclado: erro interno {e.GetType().Name} {e.Message}");
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ───────────── interop ─────────────

    private const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_TIMER = 0x0113, WM_RUN = 0x8001;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string name, int style, int x, int y,
        int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
}
