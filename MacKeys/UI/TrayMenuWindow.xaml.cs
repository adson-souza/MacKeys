using System.Windows;
using System.Windows.Media.Animation;

namespace MacKeys.UI;

/// <summary>macOS-style menu that pops up above the tray icon.</summary>
public partial class TrayMenuWindow : Window
{
    // Margin of the MacFrame that holds the shadow; the visible menu starts inside it.
    private const double ShadowMargin = 18;
    private const double ScreenGap = 10;

    private DateTime _hiddenAt;

    public TrayMenuWindow()
    {
        InitializeComponent();
        AppIconImage.Source = AppCatalog.AppIcon;
        App.Current.EnabledChanged += Sync;
        Sync();
    }

    /// <summary>Opens the menu, or closes it if it is already open.</summary>
    public void Toggle()
    {
        // Clicking the tray icon deactivates (and hides) the menu just before this runs.
        if (IsVisible || (DateTime.Now - _hiddenAt).TotalMilliseconds < 250)
        {
            HideMenu();
            return;
        }

        Sync();
        Opacity = 0;
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth + ShadowMargin - ScreenGap;
        Top = area.Bottom - ActualHeight + ShadowMargin - ScreenGap;
        Activate();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }

    private void HideMenu()
    {
        if (IsVisible) _hiddenAt = DateTime.Now;
        Hide();
    }

    private void Sync()
    {
        bool enabled = App.Current.Settings.Enabled;
        EnabledSwitch.IsChecked = enabled;
        StatusText.Text = enabled ? "Atalhos do Mac ligados" : "Pausado: teclado normal";
    }

    private void Window_Deactivated(object? sender, EventArgs e) => HideMenu();

    private void EnabledSwitch_Click(object sender, RoutedEventArgs e) =>
        App.Current.SetEnabled(EnabledSwitch.IsChecked == true);

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        HideMenu();
        App.Current.ShowSettings(SettingsPage.General);
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        HideMenu();
        App.Current.ShowSettings(SettingsPage.Shortcuts);
    }

    private void Apps_Click(object sender, RoutedEventArgs e)
    {
        HideMenu();
        App.Current.ShowSettings(SettingsPage.Apps);
    }

    private void Unstick_Click(object sender, RoutedEventArgs e)
    {
        HideMenu();
        App.Current.Mapper.Reset();
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => App.Current.Quit();
}
