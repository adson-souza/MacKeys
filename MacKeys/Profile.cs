namespace MacKeys;

/// <summary>What ⌘+arrows do.</summary>
internal enum ArrowMode
{
    /// <summary>⌘←/→ = Home/End, ⌘↑/↓ = top/bottom of document (macOS text fields).</summary>
    Line,
    /// <summary>⌘←/→ = Home/End, ⌘↑/↓ = previous/next paragraph (Word for Mac).</summary>
    Paragraph,
    /// <summary>⌘+arrow = Ctrl+arrow (Excel data edges, Adobe nudges...).</summary>
    Ctrl,
}

/// <summary>What the ⌥ key (physical Left Win) becomes.</summary>
internal enum OptionMode
{
    /// <summary>⌥←/→ jump words, ⌥⌫ deletes a word, other combos act as Win.</summary>
    Mac,
    /// <summary>⌥ is plain Alt, as Mac versions of pro apps expect.</summary>
    Alt,
}

/// <summary>How the layers behave in a given app. ⌘ + click/scroll is always Ctrl, ⌥ + click/scroll is always Alt.</summary>
internal sealed record Profile(
    string Name,
    string Description,
    ArrowMode Arrows,
    bool LineDelete,    // ⌘⌫ / ⌘⌦ delete to start/end of line
    bool Minimize,      // ⌘M / ⌘H minimize the window
    bool CmdSpaceZoom,  // ⌘Space → Ctrl+Space (Adobe zoom tool) instead of Windows search
    OptionMode Option,
    bool FinderShortcuts = false) // File Explorer: ⌘P preview pane, ⌘↑ parent folder, ⌘⌫ delete...
{
    /// <summary>File Explorer behaving like Finder, including the Alt shortcuts ⌘ took over (Alt+P, Alt+↑...).</summary>
    public static readonly Profile Explorer = new("Explorador",
        "Atalhos do Finder: ⌘P painel de visualização, ⌘↑ pasta acima, ⌘↓ abrir, ⌘[ ⌘] voltar e avançar, ⌘⌫ apagar, ⌘I propriedades.",
        ArrowMode.Line, false, true, false, OptionMode.Mac, FinderShortcuts: true);

    public static readonly Profile Default = new("Padrão",
        "Atalhos do Mac completos: ⌘ vira Ctrl e ⌘/⌥ + setas navegam no texto.",
        ArrowMode.Line, true, true, false, OptionMode.Mac);

    public static readonly Profile Document = new("Documento",
        "Como o Word para Mac: ⌘↑/↓ pulam parágrafos.",
        ArrowMode.Paragraph, true, true, false, OptionMode.Mac);

    public static readonly Profile Spreadsheet = new("Planilha",
        "Como o Excel para Mac: ⌘ + setas vão até a borda dos dados.",
        ArrowMode.Ctrl, false, true, false, OptionMode.Mac);

    /// <summary>Adobe, Autodesk, video editors: ⌘ is pure Ctrl and ⌥ is pure Alt, like their Mac versions.</summary>
    public static readonly Profile Creative = new("Criativo",
        "⌘ = Ctrl e ⌥ = Alt, sem truques. Os atalhos do app ficam iguais aos da versão Mac.",
        ArrowMode.Ctrl, false, false, true, OptionMode.Alt);

    public const string DisabledName = "Desativado";

    public static readonly IReadOnlyList<Profile> All = new[] { Default, Explorer, Document, Spreadsheet, Creative };

    /// <summary>Profile names offered in the UI, including "Desativado" (keyboard left untouched).</summary>
    public static readonly IReadOnlyList<string> Choices = All.Select(p => p.Name).Append(DisabledName).ToArray();

    public static Profile? ByName(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
