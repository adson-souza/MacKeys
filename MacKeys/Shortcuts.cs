namespace MacKeys;

/// <summary>Special mappings that can be turned off individually (they then fall back to plain ⌘ = Ctrl).</summary>
internal enum ShortcutId
{
    AppSwitcher,
    Quit,
    Minimize,
    Search,
    Emoji,
    TextNavigation,
    LineDelete,
    Screenshot,
    ScreenSnip,
    ForceQuit,
    WordNavigation,
    LockScreen,
    MouseModifiers,
    WordDelete,
    QuickLook,
    FinderShortcuts,
    MiddleClickTaskView,
}

internal sealed record ShortcutInfo(ShortcutId Id, string Group, string Keys, string Title, string Windows);

internal static class Shortcuts
{
    public static readonly IReadOnlyList<ShortcutInfo> All = new ShortcutInfo[]
    {
        new(ShortcutId.AppSwitcher, "Janelas", "⌘ Tab", "Alternar entre apps", "Alt+Tab"),
        new(ShortcutId.Quit, "Janelas", "⌘ Q", "Fechar o app", "Alt+F4"),
        new(ShortcutId.Minimize, "Janelas", "⌘ M · ⌘ H", "Minimizar a janela", "Minimizar"),
        new(ShortcutId.ForceQuit, "Janelas", "⌘ ⌥ Esc", "Forçar encerrar", "Gerenciador de Tarefas"),

        new(ShortcutId.TextNavigation, "Texto", "⌘ ← → ↑ ↓", "Início e fim da linha ou do documento", "Home / End"),
        new(ShortcutId.LineDelete, "Texto", "⌘ ⌫", "Apagar até o início da linha", "Shift+Home, ⌫"),
        new(ShortcutId.WordNavigation, "Texto", "⌥ ← →", "Pular palavras com ⌥",
            "Ctrl+← →. Desligado, ⌥ + setas organizam as janelas (Win+setas)"),
        new(ShortcutId.WordDelete, "Texto", "⌥ ⌫", "Apagar palavra", "Ctrl+⌫"),

        new(ShortcutId.Search, "Sistema", "⌘ Espaço", "Pesquisar (Spotlight)", "Paleta de Comandos do PowerToys ou Pesquisa do Windows (escolha em Geral)"),
        new(ShortcutId.Emoji, "Sistema", "⌘ ⌃ Espaço", "Emojis", "Win+."),
        new(ShortcutId.Screenshot, "Sistema", "⌘ ⇧ 3", "Capturar a tela inteira", "Print Screen"),
        new(ShortcutId.ScreenSnip, "Sistema", "⌘ ⇧ 4 · ⌘ ⇧ 5", "Capturar uma área", "Win+Shift+S"),
        new(ShortcutId.LockScreen, "Sistema", "⌥ L", "Bloquear o computador", "Win+L"),

        new(ShortcutId.QuickLook, "Arquivos", "Espaço", "Pré-visualizar o arquivo selecionado (Quick Look)",
            "QuickLook, de QL-Win. Espaço ou Esc fecham, as setas trocam de arquivo, Enter abre"),
        new(ShortcutId.FinderShortcuts, "Arquivos", "⌘ P · ⌘ ↑ · ⌘ ⌫", "Atalhos do Finder no Explorador",
            "painel de visualização (Alt+P), pasta acima, abrir (⌘↓), voltar/avançar (⌘[ ⌘]), apagar, propriedades (⌘I)"),

        new(ShortcutId.MiddleClickTaskView, "Mouse", "clique do meio", "Ver todas as janelas (Mission Control)",
            "Visão de Tarefas (Win+Tab). Arrastar com o botão do meio continua normal; exceções em Apps"),
        new(ShortcutId.MouseModifiers, "Mouse", "⌘ / ⌥ + clique", "Clicar, arrastar e rolar com ⌘ e ⌥", "Ctrl / Alt + clique"),
    };

    public static ShortcutInfo Get(ShortcutId id) => All.First(s => s.Id == id);
}
