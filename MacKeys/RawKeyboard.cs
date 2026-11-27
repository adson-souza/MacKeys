using System.Runtime.InteropServices;
using static MacKeys.Native;

namespace MacKeys;

/// <summary>
/// Listens to the physical keyboard through Raw Input. It runs in a separate helper process
/// (`MacKeys.exe --raw-helper`): registering Raw Input inside the main process, on any thread, made Windows
/// skip MacKeys' low-level hook at random (found by the self-test), while Raw Input in another process
/// doesn't affect it. The helper posts each physical key to the hook thread's window.
/// </summary>
internal sealed class RawKeyboard : IDisposable
{
    /// <summary>Helper process entry point: forwards physical keys to the main process until it exits.</summary>
    public static void RunHelper(IntPtr target, int parentPid, bool acceptInjected)
    {
        _acceptInjected = acceptInjected;
        using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
        using var raw = new RawKeyboard(key =>
            PostMessage(target, HookHost.WM_RAWKEY, (IntPtr)(int)key.Key, key.IsDown ? (IntPtr)1 : IntPtr.Zero));
        raw.Start();
        parent.WaitForExit();
    }

    public readonly record struct RawKey(Keys Key, bool IsDown);

    // Self-test only: SendInput keys stand in for the physical keyboard (except MacKeys' own).
    private static bool _acceptInjected;

    private readonly Action<RawKey> _onKey;
    private readonly WndProc _wndProc;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private IntPtr _hwnd;

    public RawKeyboard(Action<RawKey> onKey)
    {
        _onKey = onKey;
        _wndProc = WindowProc;
    }

    /// <summary>False if Raw Input couldn't be registered (the watchdog then stays idle).</summary>
    public bool Works { get; private set; }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "MacKeys raw input" };
        _thread.Start();
        _ready.Wait();
    }

    private void Run()
    {
        try
        {
            var cls = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = "MacKeysRawInputWindow",
            };
            RegisterClassEx(ref cls);
            _hwnd = CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
            var device = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd };
            Works = _hwnd != IntPtr.Zero && RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            if (!Works) DebugLog.Write($"Teclado: Raw Input indisponível ({Marshal.GetLastWin32Error()}); sem vigia de teclas presas");
        }
        finally
        {
            _ready.Set();
        }
        if (!Works) return;

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INPUT:
                try
                {
                    if (Read(lParam) is { } key) _onKey(key);
                }
                catch (Exception e)
                {
                    DebugLog.Write($"Raw Input: erro {e.GetType().Name} {e.Message}");
                }
                break; // DefWindowProc must still run for WM_INPUT
            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static RawKey? Read(IntPtr handle)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(handle, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(handle, RID_INPUT, buffer, ref size, headerSize) != size) return null;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType != RIM_TYPEKEYBOARD) return null;
            var kb = Marshal.PtrToStructure<RAWKEYBOARD>(buffer + (int)headerSize);
            // Injected input (ours, or any other tool's) has no device: only the real keyboard counts.
            // The self-test types with SendInput, so there it stands in for the keyboard (except our own keys).
            bool physical = header.hDevice != IntPtr.Zero;
            bool testKey = _acceptInjected && kb.ExtraInformation != (uint)KeyInjector.Marker.ToInt64();
            if (!physical && !testKey) return null;
            bool e0 = (kb.Flags & RI_KEY_E0) != 0;
            Keys key = kb.VKey switch
            {
                0x10 => kb.MakeCode == 0x36 ? Keys.RShiftKey : Keys.LShiftKey,
                0x11 => e0 ? Keys.RControlKey : Keys.LControlKey,
                0x12 => e0 ? Keys.RMenu : Keys.LMenu,
                _ => (Keys)kb.VKey,
            };
            return new RawKey(key, (kb.Flags & RI_KEY_BREAK) == 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ───────────── interop ─────────────

    private const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003, RIM_TYPEKEYBOARD = 1, RIDEV_INPUTSINK = 0x100;
    private const ushort RI_KEY_BREAK = 1, RI_KEY_E0 = 2;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage, usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType, dwSize;
        public IntPtr hDevice, wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode, Flags, Reserved, VKey;
        public uint Message, ExtraInformation;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr handle, uint command, IntPtr data, ref uint size, uint headerSize);
}
