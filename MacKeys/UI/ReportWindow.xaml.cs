using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace MacKeys.UI;

/// <summary>Writes a problem report or suggestion and opens it as a prefilled GitHub issue (or copies it).</summary>
public partial class ReportWindow : Window
{
    private readonly string _technical;

    public ReportWindow()
    {
        InitializeComponent();
        L.Translate(this);
        Icon = AppCatalog.AppIcon;
        _technical = BugReport.TechnicalInfo(App.Current.Settings);
        TechnicalBox.Text = _technical;
        CrashNotice.Visibility = BugReport.PendingCrashFile != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
        Loaded += (_, _) => TitleBox.Focus();
    }

    private bool IsProblem => ProblemChoice.IsChecked == true;

    private string Title_ => TitleBox.Text.Trim() is { Length: > 0 } title ? title
        : L.T(IsProblem ? "Problema relatado pelo app" : "Sugestão enviada pelo app");

    private string Body => BugReport.Compose(IsProblem, DescriptionBox.Text,
        TechnicalSwitch.IsChecked == true ? _technical : null);

    private void UpdateButtons()
    {
        DescriptionHint.Visibility = DescriptionBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool ready = DescriptionBox.Text.Trim().Length > 0;
        SendButton.IsEnabled = CopyButton.IsEnabled = ready;
    }

    private void Text_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateButtons();

    private void Kind_Changed(object sender, RoutedEventArgs e)
    {
        if (DescriptionLabel == null) return; // during InitializeComponent
        DescriptionLabel.Text = L.T(IsProblem ? "O que aconteceu?" : "Qual é a sua ideia?");
        DescriptionHint.Text = L.T(IsProblem
            ? "Em qual app você estava, o que fez, o que esperava e o que aconteceu."
            : "O que o MacKeys poderia fazer, e em qual situação isso ajudaria.");
        TechnicalSwitch.IsChecked = IsProblem;
        TechnicalExpander.IsEnabled = IsProblem;
    }

    private void Technical_Click(object sender, RoutedEventArgs e) =>
        TechnicalExpander.IsEnabled = TechnicalSwitch.IsChecked == true;

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(BugReport.IssueUrl(IsProblem, Title_, Body)) { UseShellExecute = true });
        Done();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"{Title_}\n\n{Body}");
            CopiedText.Visibility = Visibility.Visible;
            if (TechnicalSwitch.IsChecked == true) BugReport.MarkCrashesReported();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            CopiedText.Text = L.T("Não consegui copiar. Tente de novo.");
            CopiedText.Visibility = Visibility.Visible;
        }
    }

    private void Done()
    {
        if (TechnicalSwitch.IsChecked == true) BugReport.MarkCrashesReported();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
