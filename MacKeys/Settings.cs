using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacKeys;

internal sealed class Settings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Holding ⌘ alone for a moment shows the shortcuts for the current app.</summary>
    public bool ShowCheatSheet { get; set; } = true;

    public bool OnboardingDone { get; set; }

    /// <summary>
    /// False (default): the Win key stays 100% Windows (snapping, Start, Win+E...) and MacKeys only adds
    /// Win + click/drag/scroll → Alt. True: Win becomes a full Mac ⌥ (word navigation, ⌥ + key → Alt in pro apps),
    /// at the cost of Windows' own Win shortcuts, which can't be re-sent reliably once intercepted.
    /// </summary>
    public bool WinAsOption { get; set; }

    /// <summary>What ⌘Space opens (outside apps where it is the zoom tool).</summary>
    public SearchTarget CmdSpaceTarget { get; set; } = SearchTarget.WindowsSearch;

    /// <summary>Raised after every save, so the keyboard thread can pick up a fresh <see cref="MapperConfig"/>.</summary>
    public event Action? Saved;

    public MapperConfig Snapshot() => new(DisabledShortcuts, AppProfiles, WinAsOption, CmdSpaceTarget);

    /// <summary>
    /// Off by default: ⌘ + arrows for line/document navigation (they get in the way of window shortcuts),
    /// and ⌥ + arrows for words (so Win + arrows keep snapping windows and moving them between monitors).
    /// </summary>
    public List<ShortcutId> DisabledShortcuts { get; set; } = new() { ShortcutId.TextNavigation, ShortcutId.WordNavigation };

    /// <summary>Process name (e.g. "Photoshop.exe") → profile name. Apps not listed use "Padrão".</summary>
    public Dictionary<string, string> AppProfiles { get; set; } = DefaultAppProfiles();

    // Lists from v0.1 settings files, folded into AppProfiles on load.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? ExcludedApps { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? CreativeApps { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? DocumentApps { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? SpreadsheetApps { get; set; }

    private static Dictionary<string, string> DefaultAppProfiles()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in new[]
                 {
                     "Photoshop.exe", "Illustrator.exe", "InDesign.exe", "Adobe Premiere Pro.exe", "AfterFX.exe",
                     "Revit.exe", "acad.exe", "CapCut.exe",
                 })
            map[app] = Profile.Creative.Name;
        map["explorer.exe"] = Profile.Explorer.Name;
        map["WINWORD.EXE"] = Profile.Document.Name;
        map["POWERPNT.EXE"] = Profile.Document.Name;
        map["EXCEL.EXE"] = Profile.Spreadsheet.Name;
        foreach (var app in new[] { "mstsc.exe", "vmconnect.exe", "VirtualBoxVM.exe", "vmware-vmx.exe", "parsecd.exe" })
            map[app] = Profile.DisabledName;
        return map;
    }

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacKeys");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    // v0.1 kept settings next to the executable.
    private static string LegacyFilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load()
    {
        string? source = File.Exists(FilePath) ? FilePath : File.Exists(LegacyFilePath) ? LegacyFilePath : null;
        Settings settings;
        try
        {
            settings = source == null
                ? new Settings()
                : JsonSerializer.Deserialize<Settings>(File.ReadAllText(source), JsonOptions) ?? new Settings();
        }
        catch (JsonException e)
        {
            // Broken file: fall back to defaults rather than refusing to start, and keep a copy to inspect.
            DebugLog.Write($"Configurações inválidas ({e.Message}); usando os padrões. Cópia em settings.invalid.json");
            try { File.Copy(source!, Path.Combine(Folder, "settings.invalid.json"), overwrite: true); } catch (IOException) { }
            return new Settings();
        }

        settings.Normalize();
        try
        {
            settings.Save();
        }
        catch (Exception e)
        {
            DebugLog.Write($"Não foi possível salvar as configurações: {e.GetType().Name} {e.Message}");
        }
        DebugLog.Write($"Configurações carregadas de {source ?? "padrões"}: {settings.AppProfiles.Count} apps, versão {settings.Version}");
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        Saved?.Invoke();
    }

    private void Normalize()
    {
        AppProfiles = new Dictionary<string, string>(AppProfiles, StringComparer.OrdinalIgnoreCase);

        void Fold(List<string>? apps, string profile)
        {
            foreach (var app in apps ?? new()) AppProfiles[app] = profile;
        }
        Fold(SpreadsheetApps, Profile.Spreadsheet.Name);
        Fold(DocumentApps, Profile.Document.Name);
        Fold(CreativeApps, Profile.Creative.Name);
        Fold(ExcludedApps, Profile.DisabledName);
        ExcludedApps = CreativeApps = DocumentApps = SpreadsheetApps = null;

        // v0.3 added the Explorer profile; bring older settings files up to date once.
        if (Version < 3)
        {
            AppProfiles.TryAdd("explorer.exe", Profile.Explorer.Name);
            Version = 3;
        }

        // v0.4: ⌘Space opens PowerToys Command Palette when it's installed, and MacKeys' Quick Look
        // steps aside when PowerToys Peek already answers to Space (two apps can't both own the key).
        if (Version < 4)
        {
            if (PowerToys.CommandPaletteInstalled) CmdSpaceTarget = SearchTarget.CommandPalette;
            if (PowerToys.PeekUsesSpace && !DisabledShortcuts.Contains(ShortcutId.QuickLook))
                DisabledShortcuts.Add(ShortcutId.QuickLook);
            Version = 4;
        }
    }

    /// <summary>Settings format version, for one-time migrations.</summary>
    public int Version { get; set; }

    /// <summary>Profile for a process name like "Photoshop.exe"; null means leave the keyboard alone.</summary>
    public Profile? ProfileFor(string exe)
    {
        if (!AppProfiles.TryGetValue(exe, out var name)) return Profile.Default;
        if (name == Profile.DisabledName) return null;
        return Profile.ByName(name) ?? Profile.Default;
    }

    public bool IsOn(ShortcutId id) => !DisabledShortcuts.Contains(id);

    public void SetOn(ShortcutId id, bool on)
    {
        DisabledShortcuts.Remove(id);
        if (!on) DisabledShortcuts.Add(id);
        Save();
    }
}
