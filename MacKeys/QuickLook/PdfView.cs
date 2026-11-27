using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MacKeys.QuickLook;

/// <summary>
/// PDF pages rendered with the PDF engine built into Windows, stacked like in macOS Preview.
/// Also opens Illustrator files saved with PDF compatibility (the default).
/// </summary>
internal sealed class PdfView : ScrollViewer
{
    private const int MaxPages = 40;
    private const double PageWidth = 760; // DIPs

    private readonly StackPanel _pages = new() { Margin = new Thickness(0, 20, 0, 20) };
    private bool _closed;

    /// <summary>The file isn't a readable PDF (e.g. an .ai saved without PDF compatibility).</summary>
    public event Action? Failed;

    /// <summary>Why the last PDF failed to open, for diagnostics.</summary>
    internal static string? LastError { get; private set; }

    /// <summary>Page count and size of the first page, once known.</summary>
    public event Action<uint, Size>? Opened;

    public PdfView(string path)
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = _pages;
        SetResourceReference(BackgroundProperty, "Bg.Sidebar");
        Unloaded += (_, _) => _closed = true;
        _ = LoadAsync(path);
    }

    private async Task LoadAsync(string path)
    {
        PdfDocument document;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            document = await PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception e)
        {
            LastError = $"{e.GetType().Name} 0x{e.HResult:X8} {e.Message}";
            Failed?.Invoke();
            return;
        }
        if (_closed) return;

        using (var first = document.GetPage(0))
            Opened?.Invoke(document.PageCount, new Size(first.Size.Width, first.Size.Height));

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        uint count = Math.Min(document.PageCount, MaxPages);
        for (uint i = 0; i < count && !_closed; i++)
        {
            try
            {
                using var page = document.GetPage(i);
                using var stream = new InMemoryRandomAccessStream();
                var options = new PdfPageRenderOptions { DestinationWidth = (uint)(PageWidth * scale) };
                await page.RenderToStreamAsync(stream, options);
                if (_closed) return;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = stream.AsStream();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                AddPage(bitmap);
            }
            catch (Exception)
            {
                break; // a damaged page: keep what we have
            }
        }

        if (document.PageCount > MaxPages && !_closed)
        {
            var more = new TextBlock
            {
                Text = $"+ {document.PageCount - MaxPages} páginas. Abra o arquivo para ver todas.",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 8),
            };
            more.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            _pages.Children.Add(more);
        }
    }

    private void AddPage(BitmapSource bitmap)
    {
        var image = new Image { Source = bitmap, Width = PageWidth, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _pages.Children.Add(new Border
        {
            Child = image,
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(20, 0, 20, 16),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.22 },
        });
    }
}
