using System.Windows.Threading;
using static MacKeys.QuickLook.QlNative;

namespace MacKeys.QuickLook;

/// <summary>
/// Space in File Explorer opens a preview of the selection; Space or Esc closes it; the arrow keys keep
/// moving Explorer's selection and the preview follows. Leaving Explorer closes it, like on the Mac.
/// </summary>
internal sealed class QuickLookController
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly WinEventProc _foregroundProc;
    private PreviewWindow? _window;
    private IntPtr _foregroundHook;

    // Read by the keyboard thread (OnKey), written on the UI thread.
    private volatile bool _open;
    private IntPtr _source;
    private IntPtr _windowHandle;

    public QuickLookController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _foregroundProc = OnForegroundChanged;
    }

    /// <summary>
    /// Called on the keyboard thread for plain key presses (no modifiers). Returns true to swallow the key.
    /// Only cheap checks here; everything else is handed to the UI thread.
    /// </summary>
    public bool OnKey(Keys key)
    {
        IntPtr foreground = GetForegroundWindow();

        if (_open)
        {
            bool fromExplorer = foreground == Interlocked.CompareExchange(ref _source, IntPtr.Zero, IntPtr.Zero)
                                || foreground == Interlocked.CompareExchange(ref _windowHandle, IntPtr.Zero, IntPtr.Zero);
            if (fromExplorer && key is Keys.Space or Keys.Escape)
            {
                _dispatcher.BeginInvoke(Close);
                return true;
            }
            if (fromExplorer && key is Keys.Left or Keys.Right or Keys.Up or Keys.Down
                    or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown)
            {
                // Explorer moves its selection first; then we follow it.
                _dispatcher.BeginInvoke(() =>
                {
                    _refreshTimer.Stop();
                    _refreshTimer.Start();
                });
            }
            return false;
        }

        if (key == Keys.Space && ExplorerSelection.IsFileListFocused(foreground))
        {
            _dispatcher.BeginInvoke(() => Open(foreground));
            return true;
        }
        return false;
    }

    private void Open(IntPtr explorer)
    {
        if (_window != null) return;
        List<string> paths;
        try { paths = ExplorerSelection.Get(explorer); }
        catch (Exception e)
        {
            DebugLog.Write($"QL: erro ao ler a seleção: {e.GetType().Name} {e.Message}");
            return;
        }
        DebugLog.Write($"QL: seleção com {paths.Count} item(ns){(paths.Count > 0 ? ": " + Path.GetFileName(paths[0]) : "")}");
        if (paths.Count == 0) return;

        _window = new PreviewWindow();
        _window.CloseRequested += Close;
        _window.Closed += (_, _) => { if (_window != null) Close(); };
        try
        {
            _window.ShowFor(explorer, paths);
        }
        catch (Exception e)
        {
            DebugLog.Write($"QL: erro ao mostrar a janela: {e.GetType().Name} {e.Message}");
            Close();
            return;
        }
        Interlocked.Exchange(ref _source, explorer);
        Interlocked.Exchange(ref _windowHandle, _window.Handle);
        _open = true;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void Refresh()
    {
        _refreshTimer.Stop();
        if (_window == null || !IsWindow(_source)) return;
        List<string> paths;
        try { paths = ExplorerSelection.Get(_source); }
        catch (Exception) { return; }
        if (paths.Count == 0 || paths.SequenceEqual(_window.Paths, StringComparer.OrdinalIgnoreCase)) return;
        _window.ShowFor(_source, paths);
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_window == null || hwnd == _source || hwnd == _windowHandle) return;
        // Explorer's own menus and dialogs belong to it; anything else means the user went elsewhere.
        if (GetAncestor(hwnd, GA_ROOT) == _source) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        GetWindowThreadProcessId(_source, out uint explorerPid);
        if (pid == explorerPid && ClassName(hwnd) == "#32768") return; // context menu
        _dispatcher.BeginInvoke(Close);
    }

    public void Close()
    {
        _open = false;
        _refreshTimer.Stop();
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        var window = _window;
        _window = null;
        window?.Close();
        Interlocked.Exchange(ref _source, IntPtr.Zero);
        Interlocked.Exchange(ref _windowHandle, IntPtr.Zero);
    }
}
