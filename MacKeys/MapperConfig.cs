namespace MacKeys;

/// <summary>What ⌘Space opens.</summary>
internal enum SearchTarget
{
    /// <summary>PowerToys Command Palette, opened through its x-cmdpal: link.</summary>
    CommandPalette,
    /// <summary>Windows search (Win+S).</summary>
    WindowsSearch,
}

/// <summary>
/// An immutable copy of the settings the keyboard thread needs. The UI edits <see cref="Settings"/> and hands
/// the mapper a fresh snapshot after each save, so the two threads never share a mutable list.
/// </summary>
internal sealed class MapperConfig
{
    private readonly HashSet<ShortcutId> _disabled;
    private readonly Dictionary<string, string> _appProfiles;

    private readonly HashSet<string> _nativeMiddleClick;

    public MapperConfig(IEnumerable<ShortcutId> disabled, IDictionary<string, string> appProfiles, bool winAsOption,
        SearchTarget search, IEnumerable<string>? nativeMiddleClickApps = null)
    {
        _disabled = new HashSet<ShortcutId>(disabled);
        _appProfiles = new Dictionary<string, string>(appProfiles, StringComparer.OrdinalIgnoreCase);
        WinAsOption = winAsOption;
        Search = search;
        _nativeMiddleClick = new HashSet<string>(nativeMiddleClickApps ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The middle button stays normal in this app (e.g. "Revit.exe").</summary>
    public bool KeepsMiddleClick(string exe) => _nativeMiddleClick.Contains(exe);

    public bool WinAsOption { get; }

    public SearchTarget Search { get; }

    public bool IsOn(ShortcutId id) => !_disabled.Contains(id);

    /// <summary>Profile for a process name like "Photoshop.exe"; null means leave the keyboard alone.</summary>
    public Profile? ProfileFor(string exe)
    {
        if (!_appProfiles.TryGetValue(exe, out var name)) return Profile.Default;
        if (name == Profile.DisabledName) return null;
        return Profile.ByName(name) ?? Profile.Default;
    }
}
