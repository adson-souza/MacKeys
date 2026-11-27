using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MacKeys.UI;

/// <summary>The installer and uninstaller, in the style of the macOS Installer.</summary>
public partial class SetupWindow : Window
{
    private enum Step { Intro, License, Options, PowerToys, Progress, Done, Uninstall }

    private readonly bool _uninstall;
    private readonly PowerToys.State _powerToys;
    private Step _step;
    private bool _agreed;
    private bool _busy;
    private bool _succeeded;

    /// <summary>False for --screenshots, where the window is only rendered.</summary>
    internal bool ShutdownOnClose { get; set; } = true;

    internal SetupWindow(bool uninstall)
    {
        InitializeComponent();
        L.Translate(this);
        _uninstall = uninstall;
        Icon = AppCatalog.AppIcon;
        TitleIcon.Source = SideIcon.Source = IntroIcon.Source = UninstallIcon.Source = AppCatalog.AppIcon;

        try { _powerToys = uninstall ? Empty : PowerToys.ReadState(); }
        catch (Exception) { _powerToys = Empty; }

        string version = Installer.CurrentVersion;
        IntroVersion.Text = L.F("Versão {0}", version);
        if (Installer.InstalledVersion is { } installed)
        {
            UpdateNotice.Visibility = Visibility.Visible;
            UpdateText.Text = installed == version
                ? L.F("O MacKeys {0} já está instalado. Continuar vai reinstalá-lo, mantendo suas configurações.", installed)
                : L.F("O MacKeys {0} está instalado. Ele será atualizado para a versão {1}, mantendo suas configurações.", installed, version);
        }
        UninstallVersion.Text = L.F("Versão {0}", Installer.InstalledVersion ?? version);
        InstallPathText.Text = Installer.InstallDir;
        InstallPathText.ToolTip = Installer.InstallDir;

        if (QuickLookCompanion.IsUpToDate)
            QuickLookDetail.Text = L.T("Espaço pré-visualiza arquivos no Explorador. Já está instalado.");
        else
            QuickLookDetail.Text = L.F("Espaço pré-visualiza arquivos no Explorador. Baixa {0:0} MB do GitHub.", QuickLookCompanion.DownloadSize / 1e6);

        foreach (string remap in _powerToys.Remaps)
            RemapRows.Children.Add(new TextBlock { Text = "• " + remap, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });

        if (uninstall)
        {
            Title = WindowTitle.Text = L.T("Desinstalar MacKeys");
            ProgressTitle.Text = L.T("Removendo o MacKeys…");
        }
        UpdateSpace();
        Go(uninstall ? Step.Uninstall : Step.Intro);
    }

    private static PowerToys.State Empty => new(false, false, null, new(), false);

    // ───────────── steps ─────────────

    private bool ShowPeekChoice => _powerToys.PeekOn && QuickLookSwitch.IsChecked == true;
    private bool NeedsPowerToysStep => _powerToys.HasRemaps || ShowPeekChoice;

    private IEnumerable<(Step Step, string Label)> Steps()
    {
        if (_uninstall)
        {
            yield return (Step.Uninstall, "Desinstalar");
            yield return (Step.Progress, "Remoção");
            yield return (Step.Done, "Resumo");
            yield break;
        }
        yield return (Step.Intro, "Introdução");
        yield return (Step.License, "Licença");
        yield return (Step.Options, "Opções");
        if (NeedsPowerToysStep) yield return (Step.PowerToys, "PowerToys");
        yield return (Step.Progress, "Instalação");
        yield return (Step.Done, "Resumo");
    }

    private void Go(Step step)
    {
        _step = step;
        var pages = new Dictionary<Step, FrameworkElement>
        {
            [Step.Intro] = PageIntro, [Step.License] = PageLicense, [Step.Options] = PageOptions,
            [Step.PowerToys] = PagePowerToys, [Step.Progress] = PageProgress, [Step.Done] = PageDone,
            [Step.Uninstall] = PageUninstall,
        };
        foreach (var (key, page) in pages)
            page.Visibility = key == step ? Visibility.Visible : Visibility.Collapsed;
        if (pages[step] is ScrollViewer scroller) scroller.ScrollToTop();
        FadeIn(pages[step]);

        HeaderText.Text = L.T(step switch
        {
            Step.Intro => "Bem-vindo ao instalador do MacKeys",
            Step.License => "Termos de uso",
            Step.Options => "Opções de instalação",
            Step.PowerToys => "Ajustes no PowerToys",
            Step.Progress => _uninstall ? "Removendo" : "Instalando",
            Step.Done => _uninstall ? "Resumo da remoção" : "Resumo",
            _ => "Desinstalar o MacKeys",
        });

        RemapSection.Visibility = _powerToys.HasRemaps ? Visibility.Visible : Visibility.Collapsed;
        PeekSection.Visibility = ShowPeekChoice ? Visibility.Visible : Visibility.Collapsed;

        var steps = Steps().ToList();
        bool lastBeforeWork = steps.SkipWhile(s => s.Step != step).Skip(1).FirstOrDefault().Step == Step.Progress;
        NextButton.Content = L.T(step switch
        {
            Step.Done => "Fechar",
            Step.Uninstall => "Desinstalar",
            _ when lastBeforeWork => "Instalar",
            _ => "Continuar",
        });
        BackButton.Content = L.T(step == Step.Uninstall ? "Cancelar" : "Voltar");
        BackButton.Visibility = step is Step.Intro or Step.Done ? Visibility.Hidden : Visibility.Visible;
        LanguagePicker.Visibility = step is Step.Intro or Step.Uninstall ? Visibility.Visible : Visibility.Collapsed;
        // The current language reads as plain text, the other one as a link.
        PortugueseButton.IsEnabled = L.English;
        EnglishButton.IsEnabled = !L.English;
        PortugueseButton.Opacity = EnglishButton.Opacity = 1;
        (L.English ? EnglishButton : PortugueseButton).Foreground = (System.Windows.Media.Brush)FindResource("Text.Primary");
        BackButton.IsEnabled = NextButton.IsEnabled = step != Step.Progress;
        CloseButton.IsEnabled = step != Step.Progress;
        BuildStepList(steps);
    }

    private void BuildStepList(List<(Step Step, string Label)> steps)
    {
        StepList.Children.Clear();
        int current = steps.FindIndex(s => s.Step == _step);
        for (int i = 0; i < steps.Count; i++)
        {
            var dot = new Ellipse { Style = (Style)FindResource("StepDot") };
            var label = new TextBlock { Text = L.T(steps[i].Label), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            if (i == current)
            {
                dot.SetResourceReference(Shape.FillProperty, "Accent");
                label.FontWeight = FontWeights.SemiBold;
                label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            }
            else
            {
                dot.SetResourceReference(Shape.FillProperty, i < current ? "Text.Secondary" : "Text.Tertiary");
                label.SetResourceReference(TextBlock.ForegroundProperty, i < current ? "Text.Secondary" : "Text.Tertiary");
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 13) };
            row.Children.Add(dot);
            row.Children.Add(label);
            StepList.Children.Add(row);
        }
    }

    private static void FadeIn(UIElement element)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase() };
        element.BeginAnimation(OpacityProperty, fade);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Intro:
                Go(Step.License);
                break;
            case Step.License when !_agreed:
                AgreeSheet.Visibility = Visibility.Visible;
                FadeIn(AgreeSheet);
                break;
            case Step.License:
                Go(Step.Options);
                break;
            case Step.Options when NeedsPowerToysStep:
                Go(Step.PowerToys);
                break;
            case Step.Options or Step.PowerToys:
                _ = InstallAsync();
                break;
            case Step.Uninstall:
                _ = UninstallAsync();
                break;
            case Step.Done:
                Finish();
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Uninstall: Close(); break;
            case Step.License: Go(Step.Intro); break;
            case Step.Options: Go(Step.License); break;
            case Step.PowerToys: Go(Step.Options); break;
        }
    }

    private void Agree_Click(object sender, RoutedEventArgs e)
    {
        _agreed = true;
        AgreeSheet.Visibility = Visibility.Collapsed;
        Go(Step.Options);
    }

    private void Disagree_Click(object sender, RoutedEventArgs e) => Close();

    private void ReadLicense_Click(object sender, RoutedEventArgs e) => AgreeSheet.Visibility = Visibility.Collapsed;

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSpace();
        Go(_step); // the PowerToys step may come or go
    }

    private void UpdateSpace()
    {
        double app = new FileInfo(Environment.ProcessPath!).Length / 1_048_576.0;
        SpaceText.Text = QuickLookSwitch.IsChecked == true && !QuickLookCompanion.IsUpToDate
            ? L.F("Cerca de {0:0} MB, com o QuickLook", app + 270)
            : L.F("Cerca de {0:0} MB", app);
    }

    // ───────────── work ─────────────

    private async Task InstallAsync()
    {
        bool quickLook = QuickLookSwitch.IsChecked == true;
        bool peekChoice = ShowPeekChoice;
        bool useQuickLook = quickLook && !(peekChoice && UsePeekChoice.IsChecked == true);
        var options = new Installer.Options(
            QuickLook: quickLook,
            StartWithWindows: StartupSwitch.IsChecked == true,
            StartMenu: StartMenuSwitch.IsChecked == true,
            RemovePowerToysRemaps: _powerToys.HasRemaps && RemoveRemapsSwitch.IsChecked == true,
            DisablePeek: peekChoice && useQuickLook,
            UseQuickLook: useQuickLook);

        await RunAsync(async progress =>
        {
            var warnings = await Installer.InstallAsync(options, progress);
            ShowDone(true, "A instalação foi concluída.",
                warnings.Count == 0
                    ? "O MacKeys foi instalado. Procure o ⌘ perto do relógio."
                    : "O MacKeys foi instalado, com uma ressalva:",
                warnings);
        });
    }

    private async Task UninstallAsync()
    {
        bool removeSettings = RemoveSettingsSwitch.IsChecked == true;
        await RunAsync(async progress =>
        {
            await Installer.UninstallAsync(removeSettings, progress);
            ShowDone(true, "O MacKeys foi removido.",
                removeSettings ? "O app e as configurações foram apagados." :
                    "Suas configurações ficaram guardadas, caso você reinstale.", new());
            LaunchRow.Visibility = Visibility.Collapsed;
        });
    }

    private async Task RunAsync(Func<IProgress<(string Status, double Fraction)>, Task> work)
    {
        _busy = true;
        Go(Step.Progress);
        var progress = new Progress<(string Status, double Fraction)>(p =>
        {
            ProgressStatus.Text = L.T(p.Status);
            Progress.BeginAnimation(RangeBase.ValueProperty,
                new DoubleAnimation(p.Fraction, TimeSpan.FromMilliseconds(250)));
        });
        try
        {
            await work(progress);
        }
        catch (Exception ex)
        {
            Installer.Log($"Falhou: {ex}");
            ShowDone(false, _uninstall ? "A remoção falhou." : "A instalação falhou.", ex.Message, new());
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowDone(bool success, string title, string text, List<string> warnings)
    {
        _succeeded = success;
        DoneTitle.Text = L.T(title);
        DoneText.Text = L.T(text);
        WarningList.Children.Clear();
        foreach (string warning in warnings)
        {
            var box = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock { Text = L.T(warning), TextWrapping = TextWrapping.Wrap, FontSize = 12 },
            };
            box.SetResourceReference(Border.BackgroundProperty, "Bg.Window");
            WarningList.Children.Add(box);
        }
        bool clean = success && warnings.Count == 0;
        DoneCircle.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            !success ? "#FF3B30" : clean ? "#34C759" : "#FF9F0A"));
        DoneCheck.Visibility = success ? Visibility.Visible : Visibility.Collapsed;
        DoneBang.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
        LaunchRow.Visibility = success && !_uninstall ? Visibility.Visible : Visibility.Collapsed;
        ShowLogButton.Visibility = clean ? Visibility.Collapsed : Visibility.Visible;
        Go(Step.Done);
    }

    private void Finish()
    {
        if (!_uninstall && _succeeded && LaunchSwitch.IsChecked == true && File.Exists(Installer.InstalledExe))
            Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true, WorkingDirectory = Installer.InstallDir });
        Close();
    }

    // ───────────── chrome and links ─────────────

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_busy) e.Cancel = true; // never stop halfway through copying files
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!ShutdownOnClose) return;
        if (_uninstall && _succeeded) Installer.ScheduleSelfDelete();
        Application.Current.Shutdown();
    }

    private void Portuguese_Click(object sender, RoutedEventArgs e) => SwitchLanguage(AppLanguage.Portuguese);

    private void English_Click(object sender, RoutedEventArgs e) => SwitchLanguage(AppLanguage.English);

    /// <summary>Rebuilds the installer in the other language; the choice is saved with the installation.</summary>
    private void SwitchLanguage(AppLanguage language)
    {
        Installer.ChosenLanguage = language;
        L.Apply(language);
        var replacement = new SetupWindow(_uninstall) { Left = Left, Top = Top, WindowStartupLocation = WindowStartupLocation.Manual };
        replacement.Show();
        ShutdownOnClose = false;
        Close();
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void QuickLookProject_Click(object sender, RoutedEventArgs e) => Open(QuickLookCompanion.ProjectUrl);

    private void QuickLookLicense_Click(object sender, RoutedEventArgs e) => Open(QuickLookCompanion.LicenseUrl);

    private void ShowLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Installer.LogFile)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Installer.LogFile}\""));
    }

    /// <summary>For --screenshots: show one step with sample content, without doing anything.</summary>
    internal void Preview(int index)
    {
        switch (index)
        {
            case 0: Go(Step.Intro); break;
            case 1: Go(Step.License); break;
            case 2: Go(Step.Options); break;
            case 3:
                Go(Step.Progress);
                ProgressStatus.Text = L.F("Baixando o QuickLook… {0:0} de {1:0} MB", 38, 60);
                Progress.Value = 0.55;
                break;
            case 4: ShowDone(true, "A instalação foi concluída.", "O MacKeys foi instalado. Procure o ⌘ perto do relógio.", new()); break;
        }
        AgreeSheet.Visibility = Visibility.Collapsed;
        foreach (UIElement page in new UIElement[] { PageIntro, PageLicense, PageOptions, PageProgress, PageDone })
        {
            page.BeginAnimation(OpacityProperty, null);
            page.Opacity = 1;
        }
    }

    internal void PreviewAgreeSheet()
    {
        Go(Step.License);
        AgreeSheet.Visibility = Visibility.Visible;
        PageLicense.BeginAnimation(OpacityProperty, null);
        PageLicense.Opacity = 1;
    }
}
