using System.Runtime.InteropServices;
using System.Text;

namespace MacKeys.QuickLook;

/// <summary>Win32 and shell interop used only by Quick Look.</summary>
internal static class QlNative
{
    // ───────────── windows & focus ─────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? title);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    public const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // ───────────── monitors ─────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    // ───────────── window corners (Windows 11) ─────────────

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    // ───────────── foreground change notifications ─────────────

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);
    public const uint EVENT_SYSTEM_FOREGROUND = 3;
    public const uint WINEVENT_OUTOFCONTEXT = 0;

    // ───────────── file associations ─────────────

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? output, ref int size);
    private const int ASSOCSTR_FRIENDLYAPPNAME = 4;
    private const int ASSOCSTR_SHELLEXTENSION = 16;
    private const int ASSOCF_INIT_DEFAULTTOSTAR = 0x4;

    private static string? Assoc(int str, string assoc, string? extra)
    {
        int size = 0;
        AssocQueryString(ASSOCF_INIT_DEFAULTTOSTAR, str, assoc, extra, null, ref size);
        if (size <= 1) return null;
        var sb = new StringBuilder(size);
        return AssocQueryString(ASSOCF_INIT_DEFAULTTOSTAR, str, assoc, extra, sb, ref size) == 0 ? sb.ToString() : null;
    }

    /// <summary>Name of the app that opens this extension, e.g. "Fotos" or "Adobe Photoshop 2025"; null if none.</summary>
    public static string? DefaultAppName(string extension)
    {
        // Unassociated types resolve to the "Open with" picker, which isn't an app worth naming.
        // Store apps (like Photos) have no executable path, so only the picker itself is ruled out.
        var exe = Assoc(ASSOCSTR_EXECUTABLE, extension, "open");
        if (exe != null && exe.EndsWith("OpenWith.exe", StringComparison.OrdinalIgnoreCase)) return null;
        return Assoc(ASSOCSTR_FRIENDLYAPPNAME, extension, "open");
    }

    private const int ASSOCSTR_EXECUTABLE = 2;

    /// <summary>CLSID of the preview handler registered for this extension (the one Explorer's preview pane uses).</summary>
    public static Guid? PreviewHandlerFor(string extension)
    {
        var value = Assoc(ASSOCSTR_SHELLEXTENSION, extension, "{8895b1c6-b41f-4c1c-a562-0d564250836f}");
        return Guid.TryParse(value, out var clsid) ? clsid : null;
    }

    // ───────────── file type names ─────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);
    private const uint SHGFI_TYPENAME = 0x400;

    /// <summary>"Imagem PNG", "Documento do Microsoft Word"...</summary>
    public static string TypeName(string path)
    {
        var info = new SHFILEINFO();
        SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME);
        return info.szTypeName ?? "";
    }

    // ───────────── shell items ─────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    public static extern object SHCreateItemFromParsingName(string path, IntPtr bindContext, [In] ref Guid riid);

    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfa");

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern System.Runtime.InteropServices.ComTypes.IStream SHCreateStreamOnFileEx(
        string file, uint mode, uint attributes, bool create, IntPtr template);

    [DllImport("ole32.dll", PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    public static extern object CoCreateInstance([In] ref Guid clsid, IntPtr outer, uint context, [In] ref Guid iid);
    public const uint CLSCTX_LOCAL_SERVER = 0x4;
    public const uint CLSCTX_INPROC_SERVER = 0x1;

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);
}
