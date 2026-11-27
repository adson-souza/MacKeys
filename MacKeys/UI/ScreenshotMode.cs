using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacKeys.UI;

/// <summary>
/// `MacKeys.exe --screenshots <folder>` renders every window to PNG (light and dark) and exits.
/// No keyboard hook, no tray icon, no settings written: safe to run next to the real app.
/// </summary>
internal static class ScreenshotMode
{
    public static void Run(App app, string folder)
    {
        Directory.CreateDirectory(folder);
        SaveTrayPreview(Path.Combine(folder, "bandeja.png"));
        foreach (bool dark in new[] { false, true })
        {
            Theme.Apply(app.Resources, dark);
            string theme = dark ? "escuro" : "claro";

            var settings = new SettingsWindow();
            foreach (SettingsPage page in Enum.GetValues<SettingsPage>())
            {
                settings.ShowPage(page);
                Save(settings, Path.Combine(folder, $"config-{page}-{theme}.png"));
            }
            settings.Close();

            var onboarding = new OnboardingWindow();
            for (int step = 0; step < 3; step++)
            {
                onboarding.ShowStep(step);
                Save(onboarding, Path.Combine(folder, $"boas-vindas-{step + 1}-{theme}.png"));
            }
            onboarding.Close();

            var menu = new TrayMenuWindow();
            Save(menu, Path.Combine(folder, $"menu-{theme}.png"));
            menu.Close();

            var sheet = new CheatSheetWindow();
            sheet.ShowFor("Photoshop.exe", Profile.Creative, app.Settings);
            Save(sheet, Path.Combine(folder, $"cola-criativo-{theme}.png"));
            sheet.ShowFor("brave.exe", Profile.Default, app.Settings);
            Save(sheet, Path.Combine(folder, $"cola-padrao-{theme}.png"));
            sheet.Close();

            // Quick Look: an image, a text file and a folder.
            string sampleText = Path.Combine(folder, "exemplo.cs");
            File.WriteAllText(sampleText,
                "// Exemplo de código\nnamespace MacKeys;\n\ninternal static class Ola\n{\n    public static string Mensagem => \"Olá, Quick Look!\";\n}\n");
            var samples = new[]
            {
                ("imagem", Path.Combine(folder, $"config-General-{theme}.png")),
                ("texto", sampleText),
                ("pasta", folder),
            };
            foreach (var (label, sample) in samples)
            {
                var preview = new MacKeys.QuickLook.PreviewWindow();
                preview.ShowFor(IntPtr.Zero, new List<string> { sample });
                Save(preview, Path.Combine(folder, $"quicklook-{label}-{theme}.png"));
                preview.Close();
            }
        }
    }

    /// <summary>Tray icon on dark and light taskbar strips, active and paused, magnified 4×.</summary>
    private static void SaveTrayPreview(string file)
    {
        int size = TrayIcon.IconSize;
        const int zoom = 4;
        int cell = (size + 16) * zoom;
        using var sheet = new System.Drawing.Bitmap(cell * 2, cell * 2);
        using var g = System.Drawing.Graphics.FromImage(sheet);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        for (int row = 0; row < 2; row++)
        {
            bool dark = row == 0;
            using var bg = new System.Drawing.SolidBrush(dark ? System.Drawing.Color.FromArgb(32, 32, 32) : System.Drawing.Color.FromArgb(243, 243, 243));
            g.FillRectangle(bg, 0, row * cell, cell * 2, cell);
            for (int col = 0; col < 2; col++)
            {
                using var icon = TrayIcon.Draw(enabled: col == 0, darkTaskbar: dark, size);
                g.DrawImage(icon, col * cell + 8 * zoom, row * cell + 8 * zoom, size * zoom, size * zoom);
            }
        }
        sheet.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void Save(Window window, string file)
    {
        window.BeginAnimation(UIElement.OpacityProperty, null);
        window.Opacity = 1;
        window.ShowActivated = false;
        window.Left = -30000; // off-screen
        window.Top = -30000;
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

        const double scale = 1.5;
        var bitmap = new RenderTargetBitmap(
            (int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = (Visual)window.Content;
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
