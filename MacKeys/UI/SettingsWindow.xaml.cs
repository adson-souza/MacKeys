using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MacKeys.UI;

internal enum SettingsPage { General, Shortcuts, Apps, QuickLook, About }

public partial class SettingsWindow : Window
{
    private static readonly string[] PageTitles = { "Geral", "Atalhos", "Apps", "Quick Look", "Sobre" };
    private readonly UIElement[] _pages;

    private static Settings Settings => App.Current.Settings;

    public SettingsWindow()
    {
        InitializeComponent();
        L.Translate(this);
        Icon = AppCatalog.AppIcon;
        AboutIcon.Source = AppCatalog.AppIcon;
        var informational = typeof(App).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        VersionText.Text = L.F("Versão {0}", informational ?? typeof(App).Assembly.GetName().Version?.ToString(3));
        _pages = new UIElement[] { PageGeneral, PageShortcuts, PageApps, PageQuickLook, PageAbout };

        SyncSwitches();
        BuildShortcuts();
        BuildApps();
        BuildProfileLegend();
        BuildMiddleClickApps();
        BuildQuickLookOptions();

        App.Current.EnabledChanged += SyncSwitches;
        App.Current.UpdatesChanged += SyncSwitches;
        Closed += (_, _) =>
        {
            App.Current.EnabledChanged -= SyncSwitches;
            App.Current.UpdatesChanged -= SyncSwitches;
        };
        Activated += (_, _) => SyncSwitches(); // may change in Windows Settings or the tray menu
    }

    internal void ShowPage(SettingsPage page) => Nav.SelectedIndex = (int)page;

    internal SettingsPage CurrentPage => Nav.SelectedIndex < 0 ? SettingsPage.General : (SettingsPage)Nav.SelectedIndex;

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedIndex < 0) return;
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = L.T(PageTitles[Nav.SelectedIndex]);
        Scroller.ScrollToTop();
    }

    // ───────────── window chrome ─────────────

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // ───────────── Geral ─────────────

    private void SyncSwitches()
    {
        EnabledSwitch.IsChecked = Settings.Enabled;
        StartupSwitch.IsChecked = Startup.IsEnabled;
        CheatSheetSwitch.IsChecked = Settings.ShowCheatSheet;
        WinOptionSwitch.IsChecked = Settings.WinAsOption;
        CreativeAltSwitch.IsChecked = Settings.CreativeWinIsAlt;
        CreativeAltSwitch.IsEnabled = !Settings.WinAsOption; // Win as full ⌥ already covers it
        QuickLookSwitch.IsChecked = Settings.IsOn(ShortcutId.QuickLook);
        AutoUpdateSwitch.IsChecked = Settings.CheckForUpdates;
        PrereleaseSwitch.IsChecked = Settings.IncludePrereleases;
        SyncUpdate();
        SyncQuickLookInstall();
        _syncingSearch = true;
        SearchTargetCombo.SelectedIndex = Settings.CmdSpaceTarget == SearchTarget.CommandPalette ? 0 : 1;
        LanguageCombo.SelectedIndex = (int)Settings.Language;
        // "Automático" says which language it lands on.
        ((ComboBoxItem)LanguageCombo.Items[0]).Content = L.T("Automático") + (L.SystemIsPortuguese ? ": Português (Brasil)" : ": English");
        _syncingSearch = false;
    }

    private bool _syncingSearch;

    private void SearchTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSearch || SearchTargetCombo.SelectedIndex < 0) return;
        Settings.CmdSpaceTarget = SearchTargetCombo.SelectedIndex == 0 ? SearchTarget.CommandPalette : SearchTarget.WindowsSearch;
        Settings.Save();
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSearch || LanguageCombo.SelectedIndex < 0) return;
        var language = (AppLanguage)LanguageCombo.SelectedIndex;
        if (language == Settings.Language) return;
        Settings.Language = language;
        Settings.Save();
        // Let the dropdown finish closing before this window is rebuilt.
        Dispatcher.BeginInvoke(App.Current.ReloadLanguage, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void WinOptionSwitch_Click(object sender, RoutedEventArgs e)
    {
        App.Current.Mapper.Reset(); // don't leave anything held from the previous mode
        Settings.WinAsOption = WinOptionSwitch.IsChecked == true;
        Settings.Save();
        CreativeAltSwitch.IsEnabled = !Settings.WinAsOption;
    }

    private void CreativeAltSwitch_Click(object sender, RoutedEventArgs e)
    {
        App.Current.Mapper.Reset();
        Settings.CreativeWinIsAlt = CreativeAltSwitch.IsChecked == true;
        Settings.Save();
    }

    private void EnabledSwitch_Click(object sender, RoutedEventArgs e) =>
        App.Current.SetEnabled(EnabledSwitch.IsChecked == true);

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        Startup.SetEnabled(StartupSwitch.IsChecked == true);
        StartupSwitch.IsChecked = Startup.IsEnabled;
    }

    private void CheatSheetSwitch_Click(object sender, RoutedEventArgs e)
    {
        Settings.ShowCheatSheet = CheatSheetSwitch.IsChecked == true;
        Settings.Save();
    }

    private void Unstick_Click(object sender, RoutedEventArgs e) => App.Current.Mapper.Reset();

    // ───────────── Quick Look (QuickLook, by QL-Win) ─────────────

    private bool _installingQuickLook;

    private void QuickLookSwitch_Click(object sender, RoutedEventArgs e)
    {
        Settings.SetOn(ShortcutId.QuickLook, QuickLookSwitch.IsChecked == true);
        SyncQuickLookInstall();
    }

    /// <summary>QuickLook's own options (its tray menu is hidden): key in QuickLook.config, default, texts.</summary>
    private static readonly (string Key, bool Default, string Title, string Detail)[] QuickLookOptionList =
    {
        ("AutoCloseHolding", true, "Segurar Espaço para espiar", "Segure Espaço para ver o arquivo; ao soltar, a pré-visualização fecha."),
        ("CloseOnLostFocus", false, "Fechar ao clicar fora", "A pré-visualização fecha quando você clica em outra janela."),
        ("Topmost", false, "Sempre na frente", "A pré-visualização fica por cima das outras janelas."),
        ("ShowInTaskbar", false, "Mostrar na barra de tarefas", "A pré-visualização aparece como uma janela na barra de tarefas."),
        ("ShowWindowTransition", true, "Animação ao abrir", "A janela surge com uma animação suave."),
        ("UseTransparency", true, "Fundo translúcido", "Efeito de vidro do Windows atrás da pré-visualização."),
    };

    private readonly List<(CheckBox Toggle, string Key, bool Default)> _quickLookToggles = new();

    private void BuildQuickLookOptions()
    {
        QuickLookOptionRows.Children.Clear();
        _quickLookToggles.Clear();
        for (int i = 0; i < QuickLookOptionList.Length; i++)
        {
            var (key, fallback, title, detail) = QuickLookOptionList[i];
            if (i > 0) QuickLookOptionRows.Children.Add(new Border { Style = (Style)FindResource("RowSeparator") });
            var row = new Grid { Margin = new Thickness(14, 11, 14, 11) };
            var texts = new StackPanel { Margin = new Thickness(0, 0, 56, 0) };
            texts.Children.Add(new TextBlock { Text = L.T(title), Style = (Style)FindResource("RowTitle") });
            texts.Children.Add(new TextBlock { Text = L.T(detail), Style = (Style)FindResource("Secondary") });
            var toggle = new CheckBox { Style = (Style)FindResource("MacSwitch"), HorizontalAlignment = HorizontalAlignment.Right };
            toggle.Click += async (_, _) =>
            {
                bool value = toggle.IsChecked == true;
                toggle.IsEnabled = false;
                await Task.Run(() => QuickLookCompanion.SetOption(key, value));
                toggle.IsEnabled = true;
            };
            row.Children.Add(texts);
            row.Children.Add(toggle);
            QuickLookOptionRows.Children.Add(row);
            _quickLookToggles.Add((toggle, key, fallback));
        }
        SyncQuickLookOptions();
    }

    private void SyncQuickLookOptions()
    {
        foreach (var (toggle, key, fallback) in _quickLookToggles)
            toggle.IsChecked = QuickLookCompanion.GetOption(key, fallback);
        bool installed = QuickLookCompanion.IsInstalled;
        QuickLookOptions.IsEnabled = installed;
        QuickLookOptions.Opacity = installed ? 1 : 0.5;
        QuickLookSummary.Text = !installed
            ? L.T("O QuickLook ainda não está instalado.")
            : L.F("QuickLook {0}, de QL-Win.", QuickLookCompanion.InstalledVersion);
    }

    private void QuickLookPlugins_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/QL-Win/QuickLook/wiki/Available-Plugins");

    private void QuickLookData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(QuickLookCompanion.DataFolder);
        Process.Start("explorer.exe", $"\"{QuickLookCompanion.DataFolder}\"");
    }

    private async void QuickLookRestart_Click(object sender, RoutedEventArgs e)
    {
        QuickLookRestartButton.IsEnabled = false;
        bool on = Settings.IsOn(ShortcutId.QuickLook);
        await Task.Run(() =>
        {
            if (on) QuickLookCompanion.Restart();
            else QuickLookCompanion.Stop();
        });
        QuickLookRestartButton.IsEnabled = true;
    }

    private void SyncQuickLookInstall()
    {
        if (_installingQuickLook) return;
        if (_quickLookToggles.Count > 0) SyncQuickLookOptions();
        bool show = !QuickLookCompanion.IsUpToDate;
        QuickLookInstallRow.Visibility = QuickLookInstallSeparator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        long mb = (long)Math.Round(QuickLookCompanion.DownloadSize / 1e6);
        if (QuickLookCompanion.IsInstalled)
        {
            QuickLookStatus.Text = L.F("Há uma versão nova do QuickLook ({0}).", (UpdateChecker.QuickLookLatest ?? QuickLookCompanion.Pinned).Version);
            QuickLookInstallButton.Content = L.F("Atualizar ({0} MB)", mb);
        }
        else
        {
            QuickLookStatus.Text = L.T("O QuickLook ainda não está instalado.");
            QuickLookInstallButton.Content = L.F("Baixar ({0} MB)", mb);
        }
        QuickLookInstallButton.IsEnabled = true;
    }

    private async void QuickLookInstall_Click(object sender, RoutedEventArgs e)
    {
        _installingQuickLook = true;
        QuickLookInstallButton.IsEnabled = false;
        var progress = new Progress<(string Status, double Fraction)>(p => QuickLookStatus.Text = p.Status);
        try
        {
            await QuickLookCompanion.InstallAsync(progress);
            _installingQuickLook = false;
            App.Current.SyncQuickLook(force: true);
            SyncQuickLookInstall();
        }
        catch (Exception ex)
        {
            DebugLog.Write($"QuickLook: {ex}");
            _installingQuickLook = false;
            SyncQuickLookInstall();
            QuickLookStatus.Text = L.F("Não consegui baixar: {0}", ex.Message);
        }
    }

    // ───────────── updates and reports ─────────────

    private bool _updateBusy;

    private void SyncUpdate()
    {
        if (_updateBusy) return;
        var update = UpdateChecker.MacKeysUpdate;
        UpdateNotesPanel.Visibility = UpdateNotesSeparator.Visibility = update != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.IsEnabled = true;
        if (update != null)
        {
            UpdateTitle.Text = L.F(update.Prerelease ? "MacKeys {0} disponível (teste)" : "MacKeys {0} disponível", update.Version);
            UpdateDetail.Text = L.F("Você tem a {0}. Suas configurações ficam como estão.", UpdateChecker.CurrentVersion);
            UpdateButton.Content = L.T("Atualizar agora");
            string notes = update.Notes.Trim();
            UpdateNotes.Text = notes.Length > 600 ? notes[..600] + "…" : notes.Length > 0 ? notes : L.T("Sem notas para esta versão.");
            return;
        }
        UpdateButton.Content = L.T("Procurar agora");
        UpdateTitle.Text = L.T(UpdateChecker.LastCheck == null ? "Atualizações" : "O MacKeys está atualizado");
        UpdateDetail.Text = UpdateChecker.LastError is { } error ? L.F("Não consegui verificar: {0}.", L.T(error))
            : UpdateChecker.LastCheck is { } when ? L.F("Verificado às {0:HH:mm}.", when) : L.T("Ainda não verificado nesta sessão.");
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        try
        {
            if (UpdateChecker.MacKeysUpdate == null)
            {
                UpdateDetail.Text = L.T("Verificando…");
                await App.Current.CheckForUpdatesAsync();
            }
            else
            {
                await App.Current.InstallUpdateAsync(new Progress<string>(status => UpdateDetail.Text = status));
                return; // MacKeys closes and the new version opens
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Atualização: {ex}");
            _updateBusy = false;
            SyncUpdate();
            UpdateDetail.Text = L.F("A atualização falhou: {0}", ex.Message);
            return;
        }
        _updateBusy = false;
        SyncSwitches();
    }

    private void UpdatePage_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateChecker.MacKeysUpdate is { } update) OpenUrl(update.PageUrl);
    }

    private void AutoUpdateSwitch_Click(object sender, RoutedEventArgs e)
    {
        Settings.CheckForUpdates = AutoUpdateSwitch.IsChecked == true;
        Settings.Save();
    }

    private async void PrereleaseSwitch_Click(object sender, RoutedEventArgs e)
    {
        Settings.IncludePrereleases = PrereleaseSwitch.IsChecked == true;
        Settings.Save();
        await App.Current.CheckForUpdatesAsync();
    }

    private void Report_Click(object sender, RoutedEventArgs e) => App.Current.ShowReport();

    private void SelfTest_Click(object sender, RoutedEventArgs e) => App.Current.RunSelfTest();

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void QuickLookProject_Click(object sender, RoutedEventArgs e) => OpenUrl(QuickLookCompanion.ProjectUrl);

    private void QuickLookLicense_Click(object sender, RoutedEventArgs e) => OpenUrl(QuickLookCompanion.LicenseUrl);

    private void Onboarding_Click(object sender, RoutedEventArgs e) => App.Current.ShowOnboarding();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Settings.Save(); // make sure the file exists before pointing at it
        Process.Start("explorer.exe", $"/select,\"{Settings.FilePath}\"");
    }

    // ───────────── Atalhos ─────────────

    private void BuildShortcuts()
    {
        foreach (var group in Shortcuts.All.GroupBy(s => s.Group))
        {
            ShortcutGroups.Children.Add(new TextBlock { Text = L.T(group.Key), Style = (Style)FindResource("GroupHeader") });
            var rows = new StackPanel();
            bool first = true;
            foreach (var shortcut in group)
            {
                if (!first) rows.Children.Add(Separator());
                first = false;

                var toggle = new CheckBox
                {
                    Style = (Style)FindResource("MacSwitch"),
                    IsChecked = Settings.IsOn(shortcut.Id),
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                var id = shortcut.Id;
                toggle.Click += (_, _) => Settings.SetOn(id, toggle.IsChecked == true);

                var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = L.T(shortcut.Title), Style = (Style)FindResource("RowTitle") });
                text.Children.Add(new TextBlock { Text = L.F("No Windows: {0}", L.T(shortcut.Windows)), Style = (Style)FindResource("Secondary") });

                var row = new Grid { Margin = new Thickness(14, 10, 14, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var caps = new KeyCaps { Keys = shortcut.Keys };
                Grid.SetColumn(text, 1);
                Grid.SetColumn(toggle, 2);
                row.Children.Add(caps);
                row.Children.Add(text);
                row.Children.Add(toggle);
                rows.Children.Add(row);
            }
            ShortcutGroups.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = rows });
        }
    }

    // ───────────── Apps ─────────────

    private void BuildApps()
    {
        AppRows.Children.Clear();
        var apps = Settings.AppProfiles
            .Select(kv => (Exe: kv.Key, Profile: kv.Value, Name: AppCatalog.DisplayName(kv.Key)))
            .OrderBy(a => a.Profile == Profile.DisabledName) // disabled ones last
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (int i = 0; i < apps.Count; i++)
        {
            if (i > 0) AppRows.Children.Add(Separator());
            AppRows.Children.Add(AppRow(apps[i].Exe, apps[i].Name, apps[i].Profile));
        }
    }

    private UIElement AppRow(string exe, string name, string profile)
    {
        var row = new Grid { Margin = new Thickness(14, 8, 8, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = AppIconView(exe, name, 28);
        icon.Margin = new Thickness(0, 0, 12, 0);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, Style = (Style)FindResource("RowTitle") });
        text.Children.Add(new TextBlock { Text = exe, Style = (Style)FindResource("Secondary") });
        Grid.SetColumn(text, 1);

        var combo = new ComboBox
        {
            Style = (Style)FindResource("MacCombo"),
            ItemsSource = Profile.Choices.Select(c => new ComboBoxItem { Content = L.T(c), Tag = c }).ToList(),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 6, 0),
        };
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == profile)
                             ?? combo.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == Profile.Default.Name);
        combo.SelectionChanged += (_, _) =>
        {
            Settings.AppProfiles[exe] = (string)((ComboBoxItem)combo.SelectedItem).Tag;
            Settings.Save();
        };
        Grid.SetColumn(combo, 2);

        var remove = new Button { Style = (Style)FindResource("RemoveButton"), ToolTip = L.T("Remover da lista (volta ao perfil Padrão)") };
        remove.Click += (_, _) =>
        {
            Settings.AppProfiles.Remove(exe);
            Settings.Save();
            BuildApps();
        };
        Grid.SetColumn(remove, 3);

        row.Children.Add(icon);
        row.Children.Add(text);
        row.Children.Add(combo);
        row.Children.Add(remove);
        return row;
    }

    /// <summary>The app's real icon when we can find it, otherwise a rounded tile with its initial.</summary>
    internal static FrameworkElement AppIconView(string exe, string name, double size)
    {
        var source = AppCatalog.Icon(exe);
        if (source != null)
            return new Image { Source = source, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };

        var tile = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.24),
            Background = new SolidColorBrush(TileColor(exe)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = AppCatalog.TileLabel(exe, name),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = size * 0.42,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        return tile;
    }

    private static Color TileColor(string name)
    {
        Color[] palette =
        {
            Color.FromRgb(0x00, 0x7A, 0xFF), Color.FromRgb(0xAF, 0x52, 0xDE), Color.FromRgb(0xFF, 0x95, 0x00),
            Color.FromRgb(0x34, 0xC7, 0x59), Color.FromRgb(0xFF, 0x2D, 0x55), Color.FromRgb(0x5A, 0xC8, 0xFA),
            Color.FromRgb(0x58, 0x56, 0xD6), Color.FromRgb(0x8E, 0x8E, 0x93),
        };
        int hash = name.Aggregate(17, (h, c) => h * 31 + c);
        return palette[(hash & int.MaxValue) % palette.Length];
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddAppWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedExe == null) return;
        if (!Settings.AppProfiles.ContainsKey(dialog.SelectedExe))
        {
            Settings.AppProfiles[dialog.SelectedExe] = Profile.Default.Name; // user picks the profile in the list
            Settings.Save();
        }
        BuildApps();
    }

    // ───────────── middle-button exceptions ─────────────

    private void BuildMiddleClickApps()
    {
        MiddleClickRows.Children.Clear();
        var apps = Settings.NativeMiddleClickApps
            .Select(exe => (Exe: exe, Name: AppCatalog.DisplayName(exe)))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (apps.Count == 0)
        {
            var empty = new TextBlock { Text = L.T("Nenhum app: o clique do meio abre a Visão de Tarefas em todos."), Margin = new Thickness(14, 12, 14, 12) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            MiddleClickRows.Children.Add(empty);
            return;
        }
        for (int i = 0; i < apps.Count; i++)
        {
            if (i > 0) MiddleClickRows.Children.Add(Separator());
            string exe = apps[i].Exe;

            var row = new Grid { Margin = new Thickness(14, 8, 8, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = AppIconView(exe, apps[i].Name, 28);
            icon.Margin = new Thickness(0, 0, 12, 0);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = apps[i].Name, Style = (Style)FindResource("RowTitle") });
            text.Children.Add(new TextBlock { Text = exe, Style = (Style)FindResource("Secondary") });
            Grid.SetColumn(text, 1);

            var remove = new Button { Style = (Style)FindResource("RemoveButton"), ToolTip = L.T("Remover: o clique do meio passa a abrir a Visão de Tarefas") };
            remove.Click += (_, _) =>
            {
                Settings.NativeMiddleClickApps.RemoveAll(a => a.Equals(exe, StringComparison.OrdinalIgnoreCase));
                Settings.Save();
                BuildMiddleClickApps();
            };
            Grid.SetColumn(remove, 2);

            row.Children.Add(icon);
            row.Children.Add(text);
            row.Children.Add(remove);
            MiddleClickRows.Children.Add(row);
        }
    }

    private void AddMiddleClickApp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddAppWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedExe == null) return;
        if (!Settings.NativeMiddleClickApps.Contains(dialog.SelectedExe, StringComparer.OrdinalIgnoreCase))
        {
            Settings.NativeMiddleClickApps.Add(dialog.SelectedExe);
            Settings.Save();
        }
        BuildMiddleClickApps();
    }

    private void BuildProfileLegend()
    {
        var items = Profile.All.Select(p => (Name: p.Name, Description: p.Description))
            .Append((Name: Profile.DisabledName,
                     Description: "O MacKeys não mexe no teclado nesse app (jogos, máquinas virtuais, acesso remoto)."))
            .ToList();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) ProfileLegend.Children.Add(Separator());
            var row = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
            row.Children.Add(new TextBlock { Text = L.T(items[i].Name), Style = (Style)FindResource("RowTitle"), FontWeight = FontWeights.SemiBold });
            row.Children.Add(new TextBlock { Text = L.T(items[i].Description), Style = (Style)FindResource("Secondary") });
            ProfileLegend.Children.Add(row);
        }
    }

    private Border Separator() => new() { Style = (Style)FindResource("RowSeparator") };
}
