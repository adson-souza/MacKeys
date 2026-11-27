using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static MacKeys.QuickLook.QlNative;

namespace MacKeys.QuickLook;

/// <summary>
/// Hosts a shell preview handler (what File Explorer's preview pane uses for PDF, Office documents...).
/// Handlers are created out of process (prevhost.exe), so a misbehaving one can't take MacKeys down.
/// </summary>
internal sealed class PreviewHandlerHost : HwndHost
{
    private const uint STGM_READ = 0;
    private static readonly Guid IID_IPreviewHandler = new("8895b1c6-b41f-4c1c-a562-0d564250836f");

    private readonly string _path;
    private readonly Color _background;
    private IntPtr _hwnd;
    private IPreviewHandler? _handler;

    /// <summary>True once the handler accepted the file.</summary>
    public bool IsPreviewLoaded { get; private set; }

    /// <summary>The handler couldn't open the file; the window shows the file info instead.</summary>
    public event Action? Failed;

    public PreviewHandlerHost(string path, Color background)
    {
        _path = path;
        _background = background;
    }

    public static bool CanPreview(string path) => PreviewHandlerFor(Path.GetExtension(path)) != null;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
        _hwnd = CreateWindowEx(0, "Static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Dispatcher.BeginInvoke(Load, System.Windows.Threading.DispatcherPriority.Loaded);
        return new HandleRef(this, _hwnd);
    }

    private void Load()
    {
        var clsid = PreviewHandlerFor(Path.GetExtension(_path));
        if (clsid == null)
        {
            Failed?.Invoke();
            return;
        }
        // Out of process first (a crash can't take MacKeys down); some handlers, like Adobe's,
        // only register for in-process use, so try that second.
        foreach (uint context in new[] { CLSCTX_LOCAL_SERVER, CLSCTX_INPROC_SERVER })
        {
            if (TryLoad(clsid.Value, context)) return;
        }
        Failed?.Invoke();
    }

    /// <summary>Why the last handler failed, for diagnostics.</summary>
    internal static string? LastError { get; private set; }

    /// <summary>How the last handler was loaded, for diagnostics.</summary>
    internal static string? Diagnostics { get; private set; }

    /// <summary>Child windows the handler created inside our host, for diagnostics.</summary>
    internal string DescribeChildren()
    {
        var parts = new List<string>();
        EnumChildWindows(_hwnd, (child, _) =>
        {
            GetWindowRect(child, out var r);
            GetWindowThreadProcessId(child, out uint pid);
            parts.Add($"{ClassName(child)} {r.Right - r.Left}x{r.Bottom - r.Top} pid {pid} visível {IsWindowVisible(child)}");
            return true;
        }, IntPtr.Zero);
        GetWindowRect(_hwnd, out var own);
        return $"host {own.Right - own.Left}x{own.Bottom - own.Top}; filhos: " + (parts.Count == 0 ? "nenhum" : string.Join(" | ", parts));
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100;

    private bool TryLoad(Guid clsid, uint context)
    {
        try
        {
            Guid id = clsid, iid = IID_IPreviewHandler;
            _handler = (IPreviewHandler)CoCreateInstance(ref id, IntPtr.Zero, context, ref iid);

            // Stream first, as Explorer and PowerToys Peek do: some handlers (Office) only render when fed a stream.
            string how;
            if (_handler is IInitializeWithStream withStream)
            {
                withStream.Initialize(SHCreateStreamOnFileEx(_path, STGM_READ, 0, false, IntPtr.Zero), STGM_READ);
                how = "Stream";
            }
            else if (_handler is IInitializeWithItem withItem)
            {
                Guid itemIid = IID_IShellItem;
                withItem.Initialize(SHCreateItemFromParsingName(_path, IntPtr.Zero, ref itemIid), STGM_READ);
                how = "Item";
            }
            else if (_handler is IInitializeWithFile withFile)
            {
                withFile.Initialize(_path, STGM_READ);
                how = "File";
            }
            else
                throw new NotSupportedException("Handler without a known initializer");
            Diagnostics = $"{(context == CLSCTX_LOCAL_SERVER ? "fora do processo" : "no processo")}, {how}";

            // Like Explorer, tell the handler who is hosting it; Office handlers expect a frame.
            if (_handler is IObjectWithSite withSite)
                withSite.SetSite(new PreviewFrame());

            if (_handler is IPreviewHandlerVisuals visuals)
                visuals.SetBackgroundColor((uint)(_background.R | _background.G << 8 | _background.B << 16));

            var rect = ClientRect();
            _handler.SetWindow(_hwnd, ref rect);
            _handler.DoPreview();
            IsPreviewLoaded = true;
            // Layout may have finished while the handler was starting, and some handlers (Office) load
            // asynchronously and never repaint on their own: resize and repaint a few times as they settle.
            foreach (int ms in new[] { 0, 600, 1500, 3000 })
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (_handler == null) return;
                    var size = ClientRect();
                    try { _handler.SetRect(ref size); } catch (Exception) { /* handler went away */ }
                    RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                };
                timer.Start();
            }
            LastError = null;
            return true;
        }
        catch (Exception e)
        {
            LastError = $"{(context == CLSCTX_LOCAL_SERVER ? "fora do processo" : "no processo")}: {e.GetType().Name} 0x{e.HResult:X8} {e.Message}";
            Unload();
            return false;
        }
    }

    private RECT ClientRect()
    {
        var source = PresentationSource.FromVisual(this);
        double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        return new RECT(0, 0, (int)(ActualWidth * scale), (int)(ActualHeight * scale));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_handler == null || !IsPreviewLoaded) return;
        var rect = ClientRect();
        try { _handler.SetRect(ref rect); } catch (Exception) { /* handler went away */ }
    }

    private void Unload()
    {
        if (_handler == null) return;
        try { _handler.Unload(); } catch (Exception) { /* already gone */ }
        Marshal.FinalReleaseComObject(_handler);
        _handler = null;
        IsPreviewLoaded = false;
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Unload();
        DestroyWindow(hwnd.Handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string name, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr hwnd);
        [PreserveSig] int TranslateAccelerator(IntPtr msg);
    }

    [ComImport, Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSite
    {
        void SetSite([MarshalAs(UnmanagedType.IUnknown)] object site);
        void GetSite(ref Guid riid, out IntPtr site);
    }

    [ComImport, Guid("fec87aaf-35f9-447a-adb7-20234491401a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPreviewHandlerFrame
    {
        [PreserveSig] int GetWindowContext(IntPtr info);
        [PreserveSig] int TranslateAccelerator(IntPtr msg);
    }

    /// <summary>Minimal frame: no accelerators to share, so both calls decline.</summary>
    [ComVisible(true)]
    private sealed class PreviewFrame : IPreviewHandlerFrame
    {
        public int GetWindowContext(IntPtr info) => unchecked((int)0x80004001); // E_NOTIMPL
        public int TranslateAccelerator(IntPtr msg) => 1; // S_FALSE: not handled
    }

    [ComImport, Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandlerVisuals
    {
        void SetBackgroundColor(uint color);
        void SetFont(IntPtr logFont);
        void SetTextColor(uint color);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode);
    }

    [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithItem
    {
        void Initialize([MarshalAs(UnmanagedType.Interface)] object shellItem, uint mode);
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        void Initialize(System.Runtime.InteropServices.ComTypes.IStream stream, uint mode);
    }
}
