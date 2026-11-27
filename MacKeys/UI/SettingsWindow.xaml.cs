using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MacKeys.UI;

internal enum SettingsPage { General, Shortcuts, Apps, About }

public partial class SettingsWindow : Window
{
    private static readonly string[] PageTitles = { "Geral", "Atalhos", "Apps", "Sobre" };
    private readonly UIElement[] _pages;

    private static Settings Settings => App.Current.Settings;

    public SettingsWindow()
    {
        InitializeComponent();
        Icon = AppCatalog.AppIcon;
        AboutIcon.Source = AppCatalog.AppIcon;
        var informational = typeof(App).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        VersionText.Text = $"Versão {informational ?? typeof(App).Assembly.GetName().Version?.ToString(3)}";
        _pages = new UIElement[] { PageGeneral, PageShortcuts, PageApps, PageAbout };

        SyncSwitches();
        BuildShortcuts();
        BuildApps();
        BuildProfileLegend();

        App.Current.EnabledChanged += SyncSwitches;
        Closed += (_, _) => App.Current.EnabledChanged -= SyncSwitches;
        Activated += (_, _) => StartupSwitch.IsChecked = Startup.IsEnabled; // may change in Windows Settings
    }

    internal void ShowPage(SettingsPage page) => Nav.SelectedIndex = (int)page;

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedIndex < 0) return;
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = PageTitles[Nav.SelectedIndex];
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
        _syncingSearch = true;
        SearchTargetCombo.SelectedIndex = Settings.CmdSpaceTarget == SearchTarget.CommandPalette ? 0 : 1;
        _syncingSearch = false;
    }

    private bool _syncingSearch;

    private void SearchTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSearch || SearchTargetCombo.SelectedIndex < 0) return;
        Settings.CmdSpaceTarget = SearchTargetCombo.SelectedIndex == 0 ? SearchTarget.CommandPalette : SearchTarget.WindowsSearch;
        Settings.Save();
    }

    private void WinOptionSwitch_Click(object sender, RoutedEventArgs e)
    {
        App.Current.Mapper.Reset(); // don't leave anything held from the previous mode
        Settings.WinAsOption = WinOptionSwitch.IsChecked == true;
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
            ShortcutGroups.Children.Add(new TextBlock { Text = group.Key, Style = (Style)FindResource("GroupHeader") });
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
                text.Children.Add(new TextBlock { Text = shortcut.Title, Style = (Style)FindResource("RowTitle") });
                text.Children.Add(new TextBlock { Text = "No Windows: " + shortcut.Windows, Style = (Style)FindResource("Secondary") });

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
            ItemsSource = Profile.Choices,
            SelectedItem = Profile.Choices.FirstOrDefault(c => c == profile) ?? Profile.Default.Name,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 6, 0),
        };
        combo.SelectionChanged += (_, _) =>
        {
            Settings.AppProfiles[exe] = (string)combo.SelectedItem;
            Settings.Save();
        };
        Grid.SetColumn(combo, 2);

        var remove = new Button { Style = (Style)FindResource("RemoveButton"), ToolTip = "Remover da lista (volta ao perfil Padrão)" };
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
            row.Children.Add(new TextBlock { Text = items[i].Name, Style = (Style)FindResource("RowTitle"), FontWeight = FontWeights.SemiBold });
            row.Children.Add(new TextBlock { Text = items[i].Description, Style = (Style)FindResource("Secondary") });
            ProfileLegend.Children.Add(row);
        }
    }

    private Border Separator() => new() { Style = (Style)FindResource("RowSeparator") };
}
