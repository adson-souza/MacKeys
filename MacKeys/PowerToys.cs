using System.Text.Json;

namespace MacKeys;

/// <summary>What MacKeys needs to know about PowerToys, which also listens to the keyboard.</summary>
internal static class PowerToys
{
    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerToys");

    public static bool CommandPaletteInstalled => Directory.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages",
        "Microsoft.CommandPalette_8wekyb3d8bbwe"));

    /// <summary>Peek is enabled and opens with Space (alone) in File Explorer.</summary>
    public static bool PeekUsesSpace
    {
        get
        {
            try
            {
                using var general = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "settings.json")));
                if (!general.RootElement.TryGetProperty("enabled", out var enabled) ||
                    !enabled.TryGetProperty("Peek", out var peekOn) || !peekOn.GetBoolean())
                    return false;

                using var peek = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "Peek", "settings.json")));
                var props = peek.RootElement.GetProperty("properties");
                if (props.TryGetProperty("EnableSpaceToActivate", out var space) &&
                    space.TryGetProperty("value", out var spaceOn) && spaceOn.GetBoolean())
                    return true;

                var shortcut = props.GetProperty("ActivationShortcut");
                bool plain = !shortcut.GetProperty("win").GetBoolean() && !shortcut.GetProperty("ctrl").GetBoolean()
                             && !shortcut.GetProperty("alt").GetBoolean() && !shortcut.GetProperty("shift").GetBoolean();
                return plain && shortcut.GetProperty("code").GetInt32() == 32;
            }
            catch (Exception)
            {
                return false; // no PowerToys, or a settings format we don't know
            }
        }
    }
}
