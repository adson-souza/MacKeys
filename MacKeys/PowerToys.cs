using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    // ───────────── installer: conflicts and fixes ─────────────

    /// <summary>What conflicts with MacKeys: Keyboard Manager remappings (in plain words) and Peek.</summary>
    internal sealed record State(bool Installed, bool KeyboardManagerOn, string? RemapFile, List<string> Remaps, bool PeekOn)
    {
        public bool HasRemaps => KeyboardManagerOn && Remaps.Count > 0;
        public bool HasConflicts => HasRemaps || PeekOn;
    }

    // Remapping lists inside a Keyboard Manager profile file, as (section, subsection).
    private static readonly (string Section, string List)[] RemapLists =
    {
        ("remapKeys", "inProcess"), ("remapKeysToText", "inProcess"),
        ("remapShortcuts", "global"), ("remapShortcuts", "appSpecific"),
        ("remapShortcutsToText", "global"), ("remapShortcutsToText", "appSpecific"),
    };

    public static State ReadState()
    {
        var general = ReadJson(Path.Combine(Folder, "settings.json"));
        if (general == null) return new State(false, false, null, new(), false);

        bool kbmOn = general["enabled"]?["Keyboard Manager"]?.GetValue<bool>() == true;
        bool peekOn = general["enabled"]?["Peek"]?.GetValue<bool>() == true;

        string kbmFolder = Path.Combine(Folder, "Keyboard Manager");
        string profile = ReadJson(Path.Combine(kbmFolder, "settings.json"))?["properties"]?["activeConfiguration"]?["value"]
            ?.GetValue<string>() is { Length: > 0 } name ? name : "default";
        string remapFile = Path.Combine(kbmFolder, profile + ".json");

        var remaps = new List<string>();
        if (ReadJson(remapFile) is { } config)
        {
            foreach (var (section, list) in RemapLists)
            {
                if (config[section]?[list] is not JsonArray entries) continue;
                foreach (var entry in entries.OfType<JsonObject>())
                {
                    string target = Str(entry, "newRemapKeys") is { } keys ? FormatKeys(keys)
                        : (Str(entry, "newRemapString") ?? Str(entry, "unicodeText")) is { } text ? L.F("texto \"{0}\"", text)
                        : "?";
                    string app = Str(entry, "targetApp") is { Length: > 0 } exe ? " " + L.F("(só em {0})", exe) : "";
                    remaps.Add($"{FormatKeys(Str(entry, "originalKeys") ?? "")} → {target}{app}");
                }
            }
        }
        return new State(true, kbmOn, remapFile, remaps, peekOn);
    }

    /// <summary>Empties every remapping list of the active Keyboard Manager profile. Returns the backup path.</summary>
    public static string RemoveRemaps(string remapFile)
    {
        var config = ReadJson(remapFile) ?? throw new InvalidDataException("Arquivo de mapeamentos ilegível");
        foreach (var (section, list) in RemapLists)
            if (config[section] is JsonObject obj && obj[list] is JsonArray) obj[list] = new JsonArray();
        return SaveJson(config, remapFile);
    }

    /// <summary>Turns the Peek module on or off in PowerToys' general settings. Returns the backup path.</summary>
    public static string SetPeekEnabled(bool on)
    {
        string path = Path.Combine(Folder, "settings.json");
        var general = ReadJson(path) ?? throw new InvalidDataException("Configurações do PowerToys ilegíveis");
        if (general["enabled"] is not JsonObject enabled) throw new InvalidDataException("Formato desconhecido");
        enabled["Peek"] = on;
        return SaveJson(general, path);
    }

    /// <summary>
    /// Closes every PowerToys process (they write their settings back on exit, so edits must happen while
    /// they're closed). Returns the main executable to reopen, and false if some refused to close (elevated).
    /// </summary>
    public static (bool Closed, string? MainExe) Stop()
    {
        string? main = null;
        foreach (var p in PowerToysProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.ProcessName == "PowerToys") main ??= p.MainModule?.FileName;
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch (Exception) { /* elevated or already gone */ }
            }
        }
        Thread.Sleep(1000);
        var left = PowerToysProcesses();
        foreach (var p in left) p.Dispose();
        return (left.Count == 0, main);
    }

    public static void Start(string? mainExe)
    {
        var candidates = new[]
        {
            mainExe,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerToys", "PowerToys.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerToys", "PowerToys.exe"),
        };
        if (candidates.FirstOrDefault(c => c != null && File.Exists(c)) is { } exe)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    private static List<Process> PowerToysProcesses() => Process.GetProcesses()
        .Where(p =>
        {
            bool match = p.ProcessName.StartsWith("PowerToys", StringComparison.OrdinalIgnoreCase);
            if (!match) p.Dispose();
            return match;
        }).ToList();

    private static string? Str(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue(out string? s) ? s : null;

    private static JsonNode? ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null; }
        catch (Exception) { return null; }
    }

    private static string SaveJson(JsonNode node, string path)
    {
        string backup = $"{path}.mackeys-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(path, backup, overwrite: true);
        File.WriteAllText(path, node.ToJsonString());
        return backup;
    }

    // Virtual-key codes as PowerToys stores them ("164;67") → names a person recognizes.
    private static readonly Dictionary<int, string> KeyNames = new()
    {
        [8] = "Backspace", [9] = "Tab", [13] = "Enter", [16] = "Shift", [17] = "Ctrl", [18] = "Alt", [20] = "Caps Lock",
        [27] = "Esc", [32] = "Espaço", [33] = "Page Up", [34] = "Page Down", [35] = "End", [36] = "Home",
        [37] = "Seta ←", [38] = "Seta ↑", [39] = "Seta →", [40] = "Seta ↓", [44] = "Print Screen", [45] = "Insert",
        [46] = "Delete", [91] = "Win Esquerdo", [92] = "Win Direito", [93] = "Menu", [160] = "Shift Esquerdo",
        [161] = "Shift Direito", [162] = "Ctrl Esquerdo", [163] = "Ctrl Direito", [164] = "Alt Esquerdo",
        [165] = "Alt Direito (AltGr)",
    };

    private static string FormatKeys(string codes) => string.Join(" + ", codes
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(c => int.TryParse(c, out int code)
            ? KeyNames.TryGetValue(code, out var name) ? L.T(name)
            : code is >= 48 and <= 90 ? ((char)code).ToString()
            : code is >= 112 and <= 135 ? $"F{code - 111}"
            : L.F("tecla {0}", code)
            : c));
}
