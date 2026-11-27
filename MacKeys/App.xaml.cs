using System.Windows;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _cheatSheetTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private (string App, Profile Profile) _pendingCheatSheet;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        L.Apply(MacKeys.Settings.SavedLanguage());

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

        if (e.Args is ["--install-quicklook"])
        {
            // Downloads QuickLook into the install folder without any window (same as the button in Settings).
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await QuickLookCompanion.InstallAsync(new Progress<(string, double)>());
                    Installer.Log("QuickLook " + QuickLookCompanion.InstalledVersion + " instalado");
                }
                catch (Exception ex) { Installer.Log($"QuickLook falhou: {ex}"); }
                Shutdown();
            });
            return;
        }

        if (e.Args is ["--uninstall", ..])
        {
            // From Settings › Apps › Installed apps. The installed copy hands over to a temporary one first.
            if (Installer.HandOverUninstall())
            {
                Shutdown();
                return;
            }
            if (e.Args.Contains("--silent"))
            {
                // No window, settings kept. For scripts and tests (run from a copy outside the install folder).
                Dispatcher.BeginInvoke(async () =>
                {
                    try { await Installer.UninstallAsync(removeSettings: false, new Progress<(string, double)>()); }
                    catch (Exception ex) { Installer.Log($"Falhou: {ex}"); }
                    Shutdown();
                });
                return;
            }
            Theme.Apply(Resources);
            new SetupWindow(uninstall: true).Show();
            return;
        }

        if (Installer.ShouldRunInstaller(e.Args))
        {
            if (e.Args.Contains("--silent"))
            {
                // No window: keeps the current choices and never touches PowerToys. For scripts and tests.
                Dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        bool quickLook = Settings.Load().IsOn(ShortcutId.QuickLook);
                        await Installer.InstallAsync(new Installer.Options(QuickLook: quickLook,
                            StartWithWindows: MacKeys.Startup.HasEntry, StartMenu: true, RemovePowerToysRemaps: false,
                            DisablePeek: false, UseQuickLook: quickLook), new Progress<(string, double)>());
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Installer.InstalledExe)
                            { UseShellExecute = true, WorkingDirectory = Installer.InstallDir });
                    }
                    catch (Exception ex) { Installer.Log($"Falhou: {ex}"); }
                    Shutdown();
                });
                return;
            }

            // The release file is MacKeys itself: run from outside the install folder, it installs.
            Theme.Apply(Resources);
            new SetupWindow(uninstall: false).Show();
            return;
        }

        if (e.Args is ["--selftest", var report, ..])
        {
            // "--after <pid>": started from Settings › Sobre, which quits first so its hook is out of the way;
            // shows the report and reopens MacKeys at the end.
            int afterPid = e.Args is [_, _, "--after", var pidText] && int.TryParse(pidText, out int pid) ? pid : 0;
            Settings = new Settings(); // in memory only
            Theme.Apply(Resources);
            Dispatcher.BeginInvoke(async () =>
            {
                if (afterPid != 0) await Task.Run(() => WaitForExit(afterPid));
                await SelfTest.RunAsync(report);
                if (afterPid != 0)
                {
                    System.Diagnostics.Process.Start("notepad.exe", $"\"{report}\"");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                        { UseShellExecute = false });
                }
                Shutdown();
            });
            return;
        }

        if (e.Args is ["--screenshots", var folder, ..])
        {
            if (e.Args.Length > 2) L.Apply(e.Args[2] == "en" ? AppLanguage.English : AppLanguage.Portuguese);
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
        Mapper.CommandInElevatedWindow += hwnd => Dispatcher.BeginInvoke(() => ShowElevatedNotice(hwnd));
        _cheatSheetTimer.Tick += (_, _) => ShowCheatSheet();

        Settings.Saved += () =>
        {
            Mapper.UpdateConfig(Settings.Snapshot());
            SyncQuickLook();
        };
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

        SyncQuickLook();
        if (!Settings.OnboardingDone) ShowOnboarding();

        // Unexpected errors are kept for "Relatar um problema"; the UI ones don't take the app down.
        DispatcherUnhandledException += (_, args) =>
        {
            BugReport.SaveCrash(args.Exception, "interface");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            BugReport.SaveCrash(args.ExceptionObject as Exception ?? new Exception("?"), "processo");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            BugReport.SaveCrash(args.Exception, "tarefa");
            args.SetObserved();
        };
        if (BugReport.PendingCrashFile != null)
        {
            _hint ??= new HintWindow();
            _hint.ShowHint(L.T("O MacKeys encontrou um erro da última vez"),
                L.T("Se quiser, conte o que aconteceu em “Relatar um problema…”, no menu do ⌘."));
        }

        // Updates: a first look shortly after starting, then once a day.
        UpdateChecker.Checked += OnUpdatesChecked;
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            if (Settings.CheckForUpdates) _ = CheckForUpdatesAsync();
        };
        _updateTimer.Start();
    }

    // ───────────── updates ─────────────

    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    /// <summary>Raised on the UI thread when an update check finishes, so menus and settings can refresh.</summary>
    internal event Action? UpdatesChanged;

    internal Task CheckForUpdatesAsync() => UpdateChecker.CheckAsync(Settings.IncludePrereleases);

    private void OnUpdatesChecked()
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdatesChanged?.Invoke();
            if (UpdateChecker.MacKeysUpdate is { } update && Settings.AnnouncedUpdate != update.Version)
            {
                Settings.AnnouncedUpdate = update.Version;
                Settings.Save();
                _hint ??= new HintWindow();
                _hint.ShowHint(L.F("MacKeys {0} disponível", update.Version), L.T("Clique no ⌘ perto do relógio para atualizar."));
            }
        });
    }

    /// <summary>Downloads and runs the new installer; it closes this copy and opens the new one.</summary>
    internal async Task InstallUpdateAsync(IProgress<string> progress)
    {
        if (UpdateChecker.MacKeysUpdate is not { } update) return;
        await UpdateChecker.InstallMacKeysAsync(update, progress);
        Quit();
    }

    /// <summary>After the language changes: windows are rebuilt in the new language.</summary>
    internal void ReloadLanguage()
    {
        L.Apply(Settings.Language);
        _menu?.Close();
        _menu = null;
        _cheatSheet?.Close();
        _cheatSheet = null;
        _hint?.Close();
        _hint = null;
        _report?.Close();
        bool onboarding = _onboarding != null;
        _onboarding?.Close();
        if (_settingsWindow != null)
        {
            var page = _settingsWindow.CurrentPage;
            _settingsWindow.Close();
            ShowSettings(page);
        }
        if (onboarding) ShowOnboarding();
    }

    internal void ShowReport()
    {
        if (_report == null)
        {
            _report = new ReportWindow();
            _report.Closed += (_, _) => _report = null;
        }
        _report.Show();
        _report.Activate();
    }

    private ReportWindow? _report;

    private bool? _quickLookWanted;

    /// <summary>QuickLook (QL-Win) runs while MacKeys runs and its Quick Look setting is on.</summary>
    internal void SyncQuickLook(bool force = false)
    {
        bool wanted = Settings.IsOn(ShortcutId.QuickLook);
        if (!force && _quickLookWanted == wanted) return;
        _quickLookWanted = wanted;
        Task.Run(() => QuickLookCompanion.Sync(wanted));
    }

    private HintWindow? _hint;
    private readonly HashSet<IntPtr> _elevatedWindowsNoticed = new();

    /// <summary>Once per administrator window: explains why ⌘ doesn't work there, so it isn't mistaken for a hang.</summary>
    private void ShowElevatedNotice(IntPtr window)
    {
        if (!_elevatedWindowsNoticed.Add(window)) return;
        _hint ??= new HintWindow();
        _hint.ShowHint(L.T("⌘ não funciona em janelas de administrador"),
            L.T("O Windows bloqueia o MacKeys aqui. Use o Ctrl do teclado nesta janela."));
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

    private static void WaitForExit(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.WaitForExit(10_000);
        }
        catch (ArgumentException) { /* already gone */ }
    }

    /// <summary>Quits and runs the keyboard self-test in a fresh process, which reopens MacKeys afterwards.</summary>
    internal void RunSelfTest()
    {
        string report = Path.Combine(Path.GetTempPath(), "MacKeys-autoteste.txt");
        var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (string arg in new[] { "--selftest", report, "--after", Environment.ProcessId.ToString() })
            info.ArgumentList.Add(arg);
        System.Diagnostics.Process.Start(info);
        Quit();
    }

    internal void Quit()
    {
        _cheatSheet?.Close();
        QuickLookCompanion.Stop();
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
