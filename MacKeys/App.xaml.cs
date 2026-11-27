using System.Windows;
using System.Windows.Threading;
using MacKeys.QuickLook;
using MacKeys.UI;
using Microsoft.Win32;

namespace MacKeys;

public partial class App : Application
{
    private const string MutexName = @"Local\MacKeys.SingleInstance";
    private const string ShowSettingsEventName = @"Local\MacKeys.ShowSettings";

    public static new App Current => (App)Application.Current;

    internal Settings Settings { get; private set; } = null!;
    internal KeyMapper Mapper { get; private set; } = null!;

    /// <summary>Raised when MacKeys is paused or resumed, so open windows can sync their switches.</summary>
    internal event Action? EnabledChanged;

    private Mutex? _mutex;
    private EventWaitHandle? _showSettingsSignal;
    private TrayIcon? _tray;
    private TrayMenuWindow? _menu;
    private SettingsWindow? _settingsWindow;
    private OnboardingWindow? _onboarding;
    private CheatSheetWindow? _cheatSheet;
    private QuickLookController? _quickLook;
    private readonly DispatcherTimer _cheatSheetTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private (string App, Profile Profile) _pendingCheatSheet;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args is ["--raw-helper", var hwnd, var parent, ..] && long.TryParse(hwnd, out long target)
            && int.TryParse(parent, out int parentPid))
        {
            // Background helper: watches the physical keyboard for the main MacKeys process, no UI.
            bool test = e.Args.Contains("--test");
            Task.Run(() =>
            {
                try { RawKeyboard.RunHelper(new IntPtr(target), parentPid, test); }
                catch (Exception) { /* parent already gone */ }
            }).ContinueWith(_ => Dispatcher.BeginInvoke(Shutdown));
            return;
        }

        if (e.Args is ["--preview-test", var file, var output])
        {
            // Shows the real Quick Look window for a file, then captures it from the screen (including
            // native preview handlers, which an off-screen render can't see) and exits.
            Settings = new Settings();
            Theme.Apply(Resources);
            var preview = new PreviewWindow();
            preview.ShowFor(IntPtr.Zero, new List<string> { file });
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                preview.CaptureTo(output);
                File.WriteAllText(output + ".txt",
                    $"handler: {PreviewHandlerHost.LastError ?? "ok"} ({PreviewHandlerHost.Diagnostics})\n" +
                    $"janelas: {preview.DescribeHandler()}\npdf: {PdfView.LastError ?? "ok"}");
                preview.Close();
                Shutdown();
            };
            timer.Start();
            return;
        }

        if (e.Args is ["--selftest", var report])
        {
            Settings = new Settings(); // in memory only
            Theme.Apply(Resources);
            Dispatcher.BeginInvoke(async () =>
            {
                await SelfTest.RunAsync(report);
                Shutdown();
            });
            return;
        }

        if (e.Args is ["--screenshots", var folder])
        {
            Settings = new Settings(); // in memory only
            ScreenshotMode.Run(this, folder);
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Already running: opening MacKeys again brings up its settings instead.
            if (EventWaitHandle.TryOpenExisting(ShowSettingsEventName, out var signal)) signal.Set();
            Shutdown();
            return;
        }

        Theme.Apply(Resources);
        Settings = Settings.Load();

        Mapper = new KeyMapper(Settings.Snapshot());
        // These arrive on the keyboard thread: hop to the UI thread before touching windows or timers.
        Mapper.CommandPressed += (app, profile) => Dispatcher.BeginInvoke(() => OnCommandPressed(app, profile));
        Mapper.CommandFinished += () => Dispatcher.BeginInvoke(OnCommandFinished);
        _cheatSheetTimer.Tick += (_, _) => ShowCheatSheet();

        _quickLook = new QuickLookController(Dispatcher);
        Mapper.PlainKeyFilter = _quickLook.OnKey;

        Settings.Saved += () => Mapper.UpdateConfig(Settings.Snapshot());
        Mapper.Start();
        Mapper.SetEnabled(Settings.Enabled);

        // Locking the PC, the secure desktop (Ctrl+Alt+Del, UAC) and sleep can swallow key releases.
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _tray = new TrayIcon();
        _tray.Clicked += ShowTrayMenu;
        _tray.Update(Settings.Enabled);

        _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        ThreadPool.RegisterWaitForSingleObject(_showSettingsSignal,
            (_, _) => Dispatcher.BeginInvoke(() => ShowSettings()), null, -1, false);

        // Follow light/dark changes live.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        if (!Settings.OnboardingDone) ShowOnboarding();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) => Mapper.Reset();

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Mapper.Reset();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        Dispatcher.BeginInvoke(() =>
        {
            Theme.Apply(Resources);
            _tray?.Update(Settings.Enabled);
        });
    }

    // ───────────── state ─────────────

    internal void SetEnabled(bool enabled)
    {
        if (Settings.Enabled == enabled) return;
        Settings.Enabled = enabled;
        Settings.Save();
        Mapper.SetEnabled(enabled);
        _tray?.Update(enabled);
        EnabledChanged?.Invoke();
    }

    internal void Quit()
    {
        _quickLook?.Close();
        _cheatSheet?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (Mapper != null) Mapper.Dispose();
        _tray?.Dispose();
        _showSettingsSignal?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ───────────── windows ─────────────

    private void ShowTrayMenu()
    {
        _menu ??= new TrayMenuWindow();
        _menu.Toggle();
    }

    internal void ShowSettings(SettingsPage page = SettingsPage.General)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.ShowPage(page);
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    internal void ShowOnboarding()
    {
        if (_onboarding == null)
        {
            _onboarding = new OnboardingWindow();
            _onboarding.Closed += (_, _) => _onboarding = null;
        }
        _onboarding.Show();
        _onboarding.Activate();
    }

    // ───────────── cheat sheet: hold ⌘ alone ─────────────

    // These two run inside the keyboard hook, so they only touch timers and defer real UI work.
    private void OnCommandPressed(string app, Profile profile)
    {
        if (!Settings.ShowCheatSheet) return;
        _pendingCheatSheet = (app, profile);
        _cheatSheetTimer.Stop();
        _cheatSheetTimer.Start();
    }

    private void OnCommandFinished()
    {
        _cheatSheetTimer.Stop();
        if (_cheatSheet is { IsVisible: true })
            Dispatcher.BeginInvoke(() => _cheatSheet?.HideSheet());
    }

    private void ShowCheatSheet()
    {
        _cheatSheetTimer.Stop();
        _cheatSheet ??= new CheatSheetWindow();
        _cheatSheet.ShowFor(_pendingCheatSheet.App, _pendingCheatSheet.Profile, Settings);
    }
}
