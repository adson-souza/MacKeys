using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MacKeys.UI;

public partial class OnboardingWindow : Window
{
    private readonly UIElement[] _steps;
    private readonly Ellipse[] _dots;
    private int _step;

    public OnboardingWindow()
    {
        InitializeComponent();
        L.Translate(this);
        Icon = AppCatalog.AppIcon;
        HeroIcon.Source = AppCatalog.AppIcon;
        _steps = new UIElement[] { Step1, Step2, Step3 };
        _dots = new[] { Dot1, Dot2, Dot3 };
        CheatSheetSwitch.IsChecked = App.Current.Settings.ShowCheatSheet;
        // The installer already asked about starting with Windows; keep that answer.
        if (Installer.InstalledVersion != null) StartupSwitch.IsChecked = Startup.IsEnabled;
        ShowStep(0);
    }

    internal void ShowStep(int step)
    {
        _step = step;
        for (int i = 0; i < _steps.Length; i++)
        {
            _steps[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
            _dots[i].SetResourceReference(Shape.FillProperty, i == step ? "Accent" : "Text.Tertiary");
        }
        BackButton.Visibility = step == 0 ? Visibility.Hidden : Visibility.Visible;
        NextButton.Content = L.T(step == _steps.Length - 1 ? "Começar" : "Continuar");
        if (step == 1) PracticeBox.Focus();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < _steps.Length - 1)
        {
            ShowStep(_step + 1);
            return;
        }

        var settings = App.Current.Settings;
        settings.ShowCheatSheet = CheatSheetSwitch.IsChecked == true;
        settings.OnboardingDone = true;
        settings.Save();
        Startup.SetEnabled(StartupSwitch.IsChecked == true);
        Close();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowStep(Math.Max(0, _step - 1));

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !IsInsideTextBox(e.OriginalSource as DependencyObject))
            DragMove();
    }

    private static bool IsInsideTextBox(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is TextBoxBase) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Closing early still counts as seen, so it doesn't pop up on every start.
        App.Current.Settings.OnboardingDone = true;
        App.Current.Settings.Save();
        Close();
    }
}
