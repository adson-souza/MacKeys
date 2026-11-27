using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace MacKeys.UI;

public partial class AddAppWindow : Window
{
    /// <summary>Process name like "Photoshop.exe" once the user confirms.</summary>
    public string? SelectedExe { get; private set; }

    public AddAppWindow()
    {
        InitializeComponent();
        foreach (var app in AppCatalog.RunningApps())
        {
            var icon = SettingsWindow.AppIconView(app.Exe, app.Name, 22);
            icon.Margin = new Thickness(0, 0, 10, 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = app.Name, VerticalAlignment = VerticalAlignment.Center });
            AppList.Items.Add(new ListBoxItem { Content = row, Tag = app.Exe });
        }
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        AddButton.IsEnabled = AppList.SelectedItem != null;

    private void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppList.SelectedItem != null) Add_Click(sender, e);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        SelectedExe = (string)((ListBoxItem)AppList.SelectedItem).Tag;
        DialogResult = true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolha o programa",
            Filter = "Programas (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dialog.ShowDialog(this) != true) return;
        SelectedExe = Path.GetFileName(dialog.FileName);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
