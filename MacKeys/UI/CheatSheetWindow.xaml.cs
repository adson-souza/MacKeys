using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace MacKeys.UI;

/// <summary>
/// Overlay shown while ⌘ is held alone: the shortcuts for the app in front, given its profile.
/// It never takes focus and lets clicks through, so the app underneath keeps working.
/// </summary>
public partial class CheatSheetWindow : Window
{
    public CheatSheetWindow()
    {
        InitializeComponent();
        L.Translate(this);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
        };
    }

    internal void ShowFor(string exe, Profile profile, Settings settings)
    {
        string name = string.IsNullOrEmpty(exe) ? "Windows" : AppCatalog.DisplayName(exe);
        AppName.Text = name;
        AppIconHost.Content = SettingsWindow.AppIconView(exe, name, 36);
        ProfileName.Text = L.T(profile.Name);
        ProfileHint.Text = L.T(profile.Description);

        Columns.Children.Clear();
        foreach (var (group, rows) in CheatSheet.For(profile, settings))
        {
            var column = new StackPanel { Width = 300, Margin = new Thickness(0, 0, 24, 0) };
            column.Children.Add(new TextBlock
            {
                Text = L.T(group),
                Style = (Style)FindResource("GroupHeader"),
                Margin = new Thickness(0, 16, 0, 6),
            });
            foreach (var (keys, description) in rows)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var text = new TextBlock { Text = L.T(description), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(text, 1);
                row.Children.Add(new KeyCaps { Keys = keys });
                row.Children.Add(text);
                column.Children.Add(row);
            }
            Columns.Children.Add(column);
        }

        Opacity = 0;
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + (area.Height - ActualHeight) / 2;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    internal void HideSheet()
    {
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

/// <summary>What the cheat sheet lists for a profile, honoring shortcuts the user turned off.</summary>
internal static class CheatSheet
{
    public static List<(string Group, List<(string Keys, string Description)> Rows)> For(Profile p, Settings s)
    {
        var groups = new List<(string, List<(string, string)>)>();
        void Add(string group, params (string, string)?[] rows)
        {
            var list = rows.Where(r => r != null).Select(r => r!.Value).ToList();
            if (list.Count > 0) groups.Add((group, list));
        }
        (string, string)? If(bool condition, string keys, string text) => condition ? (keys, text) : null;
        // With Win left to Windows, the ⌥ rows don't apply (only Win + mouse does).
        bool opt = s.WinAsOption;
        bool on(ShortcutId id) => s.IsOn(id) && (opt || id is not (ShortcutId.WordNavigation or ShortcutId.WordDelete
            or ShortcutId.LockScreen or ShortcutId.ForceQuit));

        Add("Básico",
            ("⌘ C · ⌘ V", "Copiar e colar"),
            ("⌘ Z", "Desfazer"),
            ("⌘ ⇧ Z", "Refazer"),
            ("⌘ S", "Salvar"),
            ("⌘ + letra", "Qualquer atalho Ctrl + letra"),
            If(p.CmdSpaceZoom, "⌘ Espaço", "Zoom (Ctrl+Espaço)"),
            If(!p.CmdSpaceZoom && on(ShortcutId.Search), "⌘ Espaço",
                s.CmdSpaceTarget == SearchTarget.CommandPalette ? "Paleta de Comandos" : "Pesquisar no Windows"));

        Add("Janelas",
            If(on(ShortcutId.AppSwitcher), "⌘ Tab", "Alternar entre apps"),
            If(on(ShortcutId.Quit), "⌘ Q", "Fechar o app"),
            If(p.Minimize && on(ShortcutId.Minimize), "⌘ M", "Minimizar"),
            If(on(ShortcutId.ForceQuit), "⌘ ⌥ Esc", "Gerenciador de Tarefas"),
            If(!opt || !s.IsOn(ShortcutId.WordNavigation), opt ? "⌥ ← → ↑ ↓" : "Win ← → ↑ ↓", "Encaixar a janela"),
            If(!opt || !s.IsOn(ShortcutId.WordNavigation), opt ? "⌥ ⇧ ← →" : "Win ⇧ ← →", "Mover para o outro monitor"));

        bool finder = p.FinderShortcuts && on(ShortcutId.FinderShortcuts);
        Add("Finder",
            If(finder, "⌘ P", "Painel de visualização"),
            If(on(ShortcutId.QuickLook) && p.FinderShortcuts, "Espaço", "Pré-visualizar (Quick Look)"),
            If(finder, "⌘ ↑", "Pasta acima"),
            If(finder, "⌘ ↓", "Abrir"),
            If(finder, "⌘ [ · ⌘ ]", "Voltar e avançar"),
            If(finder, "⌘ ⌫", "Mover para a Lixeira"),
            If(finder, "⌘ I", "Propriedades"),
            If(finder, "⌘ 1 · 2 · 3 · 4", "Modos de exibição"));

        bool text = on(ShortcutId.TextNavigation) && !p.FinderShortcuts;
        Add("Texto",
            If(text && p.Arrows != ArrowMode.Ctrl, "⌘ ← →", "Início e fim da linha"),
            If(text && p.Arrows == ArrowMode.Line, "⌘ ↑ ↓", "Início e fim do documento"),
            If(text && p.Arrows == ArrowMode.Paragraph, "⌘ ↑ ↓", "Parágrafo anterior e seguinte"),
            If(p == Profile.Spreadsheet, "⌘ + setas", "Ir até a borda dos dados"),
            If(p.Arrows == ArrowMode.Ctrl && p != Profile.Spreadsheet, "⌘ + setas", "Ctrl + setas"),
            If(p.LineDelete && on(ShortcutId.LineDelete), "⌘ ⌫", "Apagar até o início da linha"),
            If(p.Option == OptionMode.Mac && on(ShortcutId.WordNavigation), "⌥ ← →", "Pular palavras"),
            If(p.Option == OptionMode.Mac && on(ShortcutId.WordDelete), "⌥ ⌫", "Apagar palavra"),
            If(p.Option == OptionMode.Mac && !on(ShortcutId.WordNavigation), "⌃ ← →", "Pular palavras"),
            If(p.Option == OptionMode.Alt, opt ? "⌥ + tecla" : "Win + tecla", "Alt + tecla (⌥ do Mac)"));

        string optKey = opt ? "⌥" : "Win";
        Add("Mouse",
            If(on(ShortcutId.MiddleClickTaskView), "clique do meio", "Ver todas as janelas"),
            If(on(ShortcutId.MouseModifiers), "⌘ + clique", "Ctrl + clique"),
            If(on(ShortcutId.MouseModifiers), $"{optKey} + arrastar", "Alt + arrastar (duplicar)"),
            If(on(ShortcutId.MouseModifiers), $"{optKey} + rolar", "Alt + rolar"));

        Add("Sistema",
            If(on(ShortcutId.Screenshot), "⌘ ⇧ 3", "Capturar a tela"),
            If(on(ShortcutId.ScreenSnip), "⌘ ⇧ 4", "Capturar uma área"),
            If(on(ShortcutId.Emoji), "⌘ ⌃ Espaço", "Emojis"),
            If(p.Option == OptionMode.Mac && on(ShortcutId.LockScreen), "⌥ L", "Bloquear o computador"));

        return groups;
    }
}
