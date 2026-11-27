using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static MacKeys.QuickLook.QlNative;

namespace MacKeys.QuickLook;

/// <summary>
/// The Quick Look panel. It never takes focus: File Explorer stays active, so the arrow keys keep
/// moving the selection there and Space/Esc reach MacKeys' keyboard hook, which closes it.
/// </summary>
public partial class PreviewWindow : Window
{
    private const double HeaderHeight = 49;

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif",
    };

    private static readonly HashSet<string> VideoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm",
    };

    private static readonly HashSet<string> AudioTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac", ".ogg",
    };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf",
        ".toml", ".html", ".htm", ".css", ".scss", ".js", ".ts", ".tsx", ".jsx", ".py", ".cs", ".java", ".c", ".cpp",
        ".h", ".hpp", ".go", ".rs", ".rb", ".php", ".sql", ".sh", ".ps1", ".bat", ".cmd", ".xaml", ".csproj", ".svg",
        ".lsp", ".scr",
    };

    /// <summary>
    /// Word's preview handler loads in our window but never paints (Excel's and PowerPoint's work, so those
    /// use the native handler); Word documents are converted to PDF by Word itself instead.
    /// </summary>
    private static readonly HashSet<string> WordTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".dot", ".dotx", ".rtf",
    };

    /// <summary>Plain text, where long lines should wrap instead of scrolling sideways.</summary>
    private static readonly HashSet<string> ProseTypes = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".markdown", ".log" };

    private IntPtr _owner;
    private List<string> _paths = new();
    private int _index;
    private string _currentPath = "";
    private Rect? _restoreBounds; // pixel bounds before "full screen"
    private Size _contentSize;
    private bool _keepAspect;

    private MediaElement? _media;
    private DispatcherTimer? _mediaTimer;
    private PreviewHandlerHost? _handlerHost;

    internal event Action? CloseRequested;
    internal IntPtr Handle => new WindowInteropHelper(this).Handle;

    public PreviewWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        };
        Closed += (_, _) => ClearBody();
    }

    /// <summary>Shows (or updates) the preview for the items selected in the given Explorer window.</summary>
    internal void ShowFor(IntPtr owner, List<string> paths)
    {
        var helper = new WindowInteropHelper(this);
        if (!IsVisible)
        {
            _owner = owner;
            helper.EnsureHandle();
            helper.Owner = owner; // stays above that Explorer window, like a sheet
        }
        _paths = paths;
        Load(0);
        if (!IsVisible) Show();
        Place();
    }

    internal IReadOnlyList<string> Paths => _paths;

    // ───────────── content ─────────────

    private void Load(int index)
    {
        _index = index;
        _currentPath = _paths[index];
        ClearBody();

        string path = _currentPath;
        bool isFolder = Directory.Exists(path);
        TitleText.Text = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path;
        SubtitleText.Text = Describe(path, isFolder);

        NavPanel.Visibility = _paths.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        CounterText.Text = $"{index + 1} de {_paths.Count}";

        string ext = Path.GetExtension(path);
        string? app = isFolder ? null : DefaultAppName(ext);
        OpenButton.Content = app != null ? $"Abrir com {app}" : "Abrir";

        _keepAspect = false;
        FrameworkElement content;
        if (isFolder) content = InfoView(path, true);
        else if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ai", StringComparison.OrdinalIgnoreCase))
            content = PdfPreview(path);
        else if (ImageTypes.Contains(ext) && ImageView(path) is { } image) content = image;
        else if (VideoTypes.Contains(ext) || AudioTypes.Contains(ext)) content = MediaView(path, VideoTypes.Contains(ext));
        else if (TextTypes.Contains(ext)) content = TextView(path, ext);
        else if (WordTypes.Contains(ext))
            content = OfficeConverter.CanConvert(path) ? OfficePreview(path) : InfoView(path, false);
        else if (PreviewHandlerHost.CanPreview(path)) content = HandlerView(path);
        else if (LooksLikeText(path)) content = TextView(path, ext);
        else content = InfoView(path, false);

        Body.Children.Add(content);
    }

    private void ClearBody()
    {
        if (_mediaTimer != null) { _mediaTimer.Stop(); _mediaTimer = null; }
        if (_media != null) { _media.Stop(); _media.Close(); _media = null; }
        if (_handlerHost != null) { _handlerHost.Dispose(); _handlerHost = null; }
        Body.Children.Clear();
    }

    private FrameworkElement? ImageView(string path)
    {
        try
        {
            var source = LoadImage(path);
            _contentSize = new Size(source.PixelWidth, source.PixelHeight);
            _keepAspect = true;
            SubtitleText.Text += $" · {source.PixelWidth} × {source.PixelHeight}";
            // Capped at one image pixel per screen unit, whatever DPI the file claims (72-dpi photos would grow).
            var image = new Image
            {
                Source = source,
                Stretch = Stretch.Uniform,
                MaxWidth = source.PixelWidth,
                MaxHeight = source.PixelHeight,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
        catch (Exception)
        {
            return null; // no codec for this format (e.g. HEIC without the extension): fall back
        }
    }

    /// <summary>Decodes at most ~2560 px wide and applies the EXIF orientation that phone photos rely on.</summary>
    private static BitmapSource LoadImage(string path)
    {
        var uri = new Uri(path);
        var probe = BitmapDecoder.Create(uri, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        int orientation = 1;
        try
        {
            if (probe.Metadata is BitmapMetadata meta && meta.GetQuery("/app1/ifd/{ushort=274}") is ushort value)
                orientation = value;
        }
        catch (Exception) { /* format without EXIF */ }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = uri;
        bitmap.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (probe.PixelWidth > 2560) bitmap.DecodePixelWidth = 2560;
        bitmap.EndInit();
        bitmap.Freeze();

        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        if (transform == null) return bitmap;
        var rotated = new TransformedBitmap(bitmap, transform);
        rotated.Freeze();
        return rotated;
    }

    private FrameworkElement MediaView(string path, bool isVideo)
    {
        var root = new Grid { Background = isVideo ? Brushes.Black : Brushes.Transparent };
        _contentSize = isVideo ? new Size(960, 540) : new Size(480, 360);

        if (!isVideo)
        {
            var art = ShellThumbnail.Get(path, 512, out bool isThumbnail);
            root.Children.Add(new Image
            {
                Source = art,
                Width = isThumbnail ? 220 : 128,
                Height = isThumbnail ? 220 : 128,
                Margin = new Thickness(0, 0, 0, 56),
            });
        }

        _media = new MediaElement
        {
            Source = new Uri(path),
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform,
            Volume = 0.8,
        };
        root.Children.Add(_media);

        // Controls: a floating rounded bar like QuickTime's.
        var play = new Button { Style = (Style)FindResource("NavButton"), Content = "❚❚", Foreground = Brushes.White };
        var slider = new Slider { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        var time = new TextBlock { Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Text = "0:00" };
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(time, 2);
        bar.Children.Add(play);
        bar.Children.Add(slider);
        bar.Children.Add(time);
        root.Children.Add(new Border
        {
            Child = bar,
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x20, 0x20, 0x22)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 12, 6),
            Margin = new Thickness(24, 0, 24, 16),
            MaxWidth = 560,
            VerticalAlignment = VerticalAlignment.Bottom,
        });

        bool playing = true, dragging = false;
        void TogglePlay()
        {
            if (_media == null) return;
            if (playing) _media.Pause(); else _media.Play();
            playing = !playing;
            play.Content = playing ? "❚❚" : "▶";
        }
        play.Click += (_, _) => TogglePlay();
        if (isVideo) _media.MouseLeftButtonUp += (_, _) => TogglePlay();

        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            dragging = false;
            if (_media != null) _media.Position = TimeSpan.FromSeconds(slider.Value);
        }));

        _media.MediaOpened += (_, _) =>
        {
            if (_media == null) return;
            if (_media.NaturalDuration.HasTimeSpan) slider.Maximum = _media.NaturalDuration.TimeSpan.TotalSeconds;
            if (isVideo && _media.NaturalVideoWidth > 0)
            {
                _contentSize = new Size(_media.NaturalVideoWidth, _media.NaturalVideoHeight);
                _keepAspect = true;
                SubtitleText.Text += $" · {_media.NaturalVideoWidth} × {_media.NaturalVideoHeight}";
                Place();
            }
        };
        _media.MediaEnded += (_, _) =>
        {
            if (_media == null) return;
            _media.Position = TimeSpan.Zero;
            _media.Pause();
            playing = false;
            play.Content = "▶";
        };

        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _mediaTimer.Tick += (_, _) =>
        {
            if (_media == null) return;
            var position = _media.Position;
            if (!dragging) slider.Value = position.TotalSeconds;
            time.Text = position.Hours > 0 ? position.ToString(@"h\:mm\:ss") : position.ToString(@"m\:ss");
        };
        _mediaTimer.Start();
        _media.Play();
        return root;
    }

    private FrameworkElement TextView(string path, string ext)
    {
        const int maxChars = 400_000;
        string text;
        try
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[maxChars];
            int read = reader.ReadBlock(buffer, 0, maxChars);
            text = new string(buffer, 0, read);
            if (!reader.EndOfStream) text += "\n\n… (arquivo grande: mostrando só o começo)";
        }
        catch (Exception e)
        {
            text = "Não foi possível ler o arquivo: " + e.Message;
        }

        _contentSize = new Size(860, 640);
        bool prose = ProseTypes.Contains(ext);
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(18, 14, 18, 14),
            FontFamily = new FontFamily(prose ? "Segoe UI Variable Text, Segoe UI" : "Cascadia Mono, Consolas"),
            FontSize = prose ? 14 : 13,
            TextWrapping = prose ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = prose ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            Focusable = false,
        };
        box.SetResourceReference(ForegroundProperty, "Text.Primary");
        return box;
    }

    /// <param name="path">The PDF to render.</param>
    /// <param name="original">The file being previewed, when the PDF is a converted copy of it.</param>
    private FrameworkElement PdfPreview(string path, string? original = null)
    {
        _contentSize = new Size(820, 980);
        var view = new PdfView(path);
        string shown = original ?? path;
        view.Opened += (pages, firstPage) =>
        {
            if (_currentPath != shown) return;
            SubtitleText.Text += pages == 1 ? " · 1 página" : $" · {pages} páginas";
            // Landscape documents (and most artboards) get a wider, shorter window.
            if (firstPage.Width > firstPage.Height)
            {
                _contentSize = new Size(1000, 760);
                Place();
            }
        };
        view.Failed += () =>
        {
            if (_currentPath != shown) return;
            ClearBody();
            Body.Children.Add(InfoView(shown, false));
            Place();
        };
        return view;
    }

    /// <summary>Shows the file info with a "generating" note while Office makes a PDF, then the pages.</summary>
    private FrameworkElement OfficePreview(string path)
    {
        var waiting = (StackPanel)InfoView(path, false);
        var note = new TextBlock
        {
            Text = "Gerando pré-visualização…",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        waiting.Children.Add(note);
        _contentSize = new Size(820, 980); // room for the pages that are coming

        _ = ShowOfficePdfAsync(path, note);
        return waiting;
    }

    private async Task ShowOfficePdfAsync(string path, TextBlock note)
    {
        string? pdf = await OfficeConverter.ToPdfAsync(path);
        if (_currentPath != path || !IsLoaded) return; // user moved on
        if (pdf == null)
        {
            note.Text = "Não foi possível gerar a pré-visualização.";
            return;
        }
        ClearBody();
        Body.Children.Add(PdfPreview(pdf, path));
        Place();
    }

    private FrameworkElement HandlerView(string path)
    {
        _contentSize = new Size(880, 760);
        var background = (FindResource("Bg.Window") as SolidColorBrush)?.Color ?? Colors.White;
        _handlerHost = new PreviewHandlerHost(path, background);
        _handlerHost.Failed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_currentPath != path) return;
            ClearBody();
            Body.Children.Add(InfoView(path, false));
            Place();
        });
        return _handlerHost;
    }

    /// <summary>Big thumbnail (or icon) plus the basics. Used for folders and anything we can't render.</summary>
    private FrameworkElement InfoView(string path, bool isFolder)
    {
        var thumbnail = ShellThumbnail.Get(path, 768, out bool isThumbnail);
        _contentSize = isThumbnail ? new Size(720, 560) : new Size(560, 400);

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) };
        if (thumbnail != null)
        {
            var image = new Image
            {
                Source = thumbnail,
                MaxWidth = isThumbnail ? 440 : 160,
                MaxHeight = isThumbnail ? 360 : 160,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            if (isThumbnail)
                image.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.25 };
            panel.Children.Add(image);
        }

        var name = new TextBlock
        {
            Text = Path.GetFileName(path.TrimEnd('\\')),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 18, 0, 6),
            MaxWidth = 460,
        };
        panel.Children.Add(name);

        foreach (var line in InfoLines(path, isFolder))
        {
            var text = new TextBlock { Text = line, FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 1, 0, 1) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            panel.Children.Add(text);
        }
        return panel;
    }

    private static IEnumerable<string> InfoLines(string path, bool isFolder)
    {
        var culture = new CultureInfo("pt-BR");
        if (isFolder)
        {
            int count = 0;
            try { count = Directory.EnumerateFileSystemEntries(path).Count(); } catch (Exception) { /* no access */ }
            yield return count == 1 ? "Pasta · 1 item" : $"Pasta · {count} itens";
            yield return "Modificada em " + Directory.GetLastWriteTime(path).ToString("d 'de' MMMM 'de' yyyy, HH:mm", culture);
            yield break;
        }
        var info = new FileInfo(path);
        yield return $"{TypeName(path)} · {FormatSize(info.Length)}";
        yield return "Modificado em " + info.LastWriteTime.ToString("d 'de' MMMM 'de' yyyy, HH:mm", culture);
    }

    private static string Describe(string path, bool isFolder)
    {
        if (isFolder) return "Pasta";
        try { return $"{TypeName(path)} · {FormatSize(new FileInfo(path).Length)}"; }
        catch (Exception) { return TypeName(path); }
    }

    internal static string FormatSize(long bytes)
    {
        var culture = new CultureInfo("pt-BR");
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return unit == 0 ? $"{bytes} bytes" : value.ToString(value < 10 ? "0.#" : "0", culture) + " " + units[unit];
    }

    /// <summary>Small files without NUL bytes in their first 4 KB are shown as text.</summary>
    private static bool LooksLikeText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > 5_000_000) return false;
            using var stream = info.OpenRead();
            var buffer = new byte[4096];
            int read = stream.Read(buffer, 0, buffer.Length);
            return !buffer.AsSpan(0, read).Contains((byte)0);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ───────────── placement ─────────────

    /// <summary>Centers the panel on the Explorer window's monitor, sized for the content.</summary>
    private void Place()
    {
        if (_restoreBounds != null) return; // user chose full screen
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        IntPtr monitor = MonitorFromWindow(_owner, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;

        var work = info.rcWork;
        double maxW = (work.Right - work.Left) * 0.86 / scale;
        double maxH = (work.Bottom - work.Top) * 0.86 / scale - HeaderHeight;

        double w = _contentSize.Width, h = _contentSize.Height;
        if (_keepAspect && w > 0 && h > 0)
        {
            double fit = Math.Min(1, Math.Min(maxW / w, maxH / h));
            w *= fit;
            h *= fit;
        }
        w = Math.Clamp(w, MinWidth, maxW);
        h = Math.Clamp(h + HeaderHeight, MinHeight, maxH + HeaderHeight);

        int pw = (int)(w * scale), ph = (int)(h * scale);
        int x = work.Left + (work.Right - work.Left - pw) / 2;
        int y = work.Top + (work.Bottom - work.Top - ph) / 2;
        // Twice: the first move may land on a monitor with another DPI and trigger a rescale.
        SetWindowPos(hwnd, IntPtr.Zero, x, y, pw, ph, SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, pw, ph, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void Zoom_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = Handle;
        if (_restoreBounds is { } restore)
        {
            _restoreBounds = null;
            SetWindowPos(hwnd, IntPtr.Zero, (int)restore.X, (int)restore.Y, (int)restore.Width, (int)restore.Height,
                SWP_NOZORDER | SWP_NOACTIVATE);
            return;
        }
        GetWindowRect(hwnd, out var current);
        _restoreBounds = new Rect(current.Left, current.Top, current.Right - current.Left, current.Bottom - current.Top);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info);
        var work = info.rcWork;
        SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    internal string DescribeHandler() => _handlerHost?.DescribeChildren() ?? "sem handler";

    /// <summary>Copies what is on screen inside this window to a PNG (diagnostics only).</summary>
    internal void CaptureTo(string file)
    {
        GetWindowRect(Handle, out var r);
        using var bitmap = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, bitmap.Size);
        bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }

    // ───────────── header actions ─────────────

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_currentPath) { UseShellExecute = true }); }
        catch (Exception) { /* no app for it; Windows shows its own message */ }
        CloseRequested?.Invoke();
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        Load((_index - 1 + _paths.Count) % _paths.Count);
        Place();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        Load((_index + 1) % _paths.Count);
        Place();
    }
}
