using Microsoft.Win32;

namespace MacKeys;

/// <summary>Per-user autostart via HKCU\...\Run: no admin rights needed, runs non-elevated.</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MacKeys";

    private static string Command => CommandFor(Environment.ProcessPath!);

    private static string CommandFor(string exe) => $"\"{exe}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Some MacKeys copy starts with Windows (not necessarily this one).</summary>
    public static bool HasEntry
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
    }

    /// <param name="exe">The executable to start; by default, the one running now.</param>
    public static void SetEnabled(bool enabled, string? exe = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, exe == null ? Command : CommandFor(exe));
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
