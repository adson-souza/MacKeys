using System.Runtime.InteropServices;
using static MacKeys.QuickLook.QlNative;

namespace MacKeys.QuickLook;

/// <summary>Reads what is selected in a File Explorer window (the active tab, on Windows 11).</summary>
internal static class ExplorerSelection
{
    /// <summary>
    /// True when the foreground window is File Explorer and the keyboard is in its file list
    /// (not renaming, not in the address bar or search box). Cheap enough to call from the keyboard hook.
    /// </summary>
    public static bool IsFileListFocused(IntPtr foreground)
    {
        if (ClassName(foreground) != "CabinetWClass") return false;

        // Windows 11 can run each tab on its own thread, so ask both the frame's and the active tab's.
        string focus = FocusClass(GetWindowThreadProcessId(foreground, out _));
        IntPtr tab = FindWindowEx(foreground, IntPtr.Zero, "ShellTabWindowClass", null);
        if (focus == "" && tab != IntPtr.Zero) focus = FocusClass(GetWindowThreadProcessId(tab, out _));

        bool ok = focus is "DirectUIHWND" or "SysListView32";
        DebugLog.Write($"QL: espaço no Explorador, foco em '{focus}' -> {(ok ? "abrir" : "ignorar")}");
        return ok;
    }

    private static string FocusClass(uint thread)
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(thread, ref info) && info.hwndFocus != IntPtr.Zero ? ClassName(info.hwndFocus) : "";
    }

    /// <summary>Full paths of the selected items, in Explorer's order. Must run on an STA thread.</summary>
    public static List<string> Get(IntPtr explorerWindow)
    {
        var result = new List<string>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null) return result;

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            IntPtr activeTab = FindWindowEx(explorerWindow, IntPtr.Zero, "ShellTabWindowClass", null);
            dynamic windows = shell.Windows();
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic? window = windows.Item(i);
                if (window == null) continue;
                try
                {
                    if ((IntPtr)(long)window.HWND != explorerWindow) continue;
                    // Each Explorer tab is its own "window" sharing the frame's HWND: keep the visible tab.
                    if (activeTab != IntPtr.Zero && TabWindow((object)window) is { } tab && tab != activeTab) continue;

                    dynamic document = window.Document;
                    foreach (dynamic item in document.SelectedItems())
                    {
                        string path = item.Path;
                        if (!string.IsNullOrEmpty(path)) result.Add(path);
                    }
                    // Nothing selected: preview the item with the keyboard focus, as Finder does.
                    if (result.Count == 0 && document.FocusedItem is { } focused && focused.Path is string focusedPath
                        && focusedPath.Length > 0)
                        result.Add(focusedPath);
                    if (result.Count > 0) return result;
                }
                catch (Exception)
                {
                    // Window closing, or a view without a Document (e.g. Control Panel): skip it.
                }
                finally
                {
                    Marshal.ReleaseComObject(window);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
        return result;
    }

    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    private static IntPtr? TabWindow(object window)
    {
        if (window is not IServiceProvider provider) return null;
        Guid service = SID_STopLevelBrowser, iid = IID_IShellBrowser;
        if (provider.QueryService(ref service, ref iid, out IntPtr ptr) != 0 || ptr == IntPtr.Zero) return null;
        try
        {
            var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(ptr);
            return browser.GetWindow(out IntPtr hwnd) == 0 ? hwnd : null;
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr ppv);
    }

    // Only the first vtable slot (IOleWindow.GetWindow) is used.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
    }
}
