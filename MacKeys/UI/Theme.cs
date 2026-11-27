using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MacKeys.UI;

/// <summary>macOS-like color tokens, in light and dark, following the Windows app theme.</summary>
internal static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool AppsUseDark => ReadDword("AppsUseLightTheme") == 0;

    /// <summary>The taskbar can be dark while apps are light, so the tray icon checks this separately.</summary>
    public static bool TaskbarIsDark => ReadDword("SystemUsesLightTheme") == 0;

    private static int ReadDword(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(name) is int value ? value : 1;
    }

    public static void Apply(ResourceDictionary resources, bool? forceDark = null)
    {
        bool dark = forceDark ?? AppsUseDark;
        void Set(string key, string light, string darkHex) =>
            resources[key] = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkHex : light)));

        Set("Bg.Window", "#F5F5F7", "#1E1E1E");
        Set("Bg.Sidebar", "#E9E9EB", "#2A2A2C");
        Set("Bg.Card", "#FFFFFF", "#2C2C2E");
        Set("Bg.Control", "#FFFFFF", "#3A3A3C");
        Set("Bg.Hover", "#0F000000", "#1AFFFFFF");
        Set("Bg.Menu", "#F2F6F6F6", "#F2282828");
        Set("Bg.Overlay", "#EBF6F6F6", "#E6262628");
        Set("Bg.KeyCap", "#F2F2F4", "#48484A");
        Set("Border.Card", "#E3E3E6", "#3A3A3C");
        Set("Border.Window", "#33000000", "#59FFFFFF");
        Set("Border.KeyCap", "#D1D1D6", "#5A5A5E");
        Set("Separator", "#E5E5EA", "#3A3A3C");
        Set("Text.Primary", "#1D1D1F", "#F5F5F7");
        Set("Text.Secondary", "#6E6E73", "#A1A1A6");
        Set("Text.Tertiary", "#AEAEB2", "#636366");
        Set("Text.OnAccent", "#FFFFFF", "#FFFFFF");
        Set("Accent", "#007AFF", "#0A84FF");
        Set("Accent.Pressed", "#0062CC", "#0070E0");
        Set("Switch.Off", "#E3E3E8", "#48484A");
        Set("Traffic.Close", "#FF5F57", "#FF5F57");
        Set("Traffic.Minimize", "#FEBC2E", "#FEBC2E");
        Set("Traffic.Zoom", "#28C840", "#28C840");
        Set("Traffic.Idle", "#D1D1D6", "#4A4A4D");
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
