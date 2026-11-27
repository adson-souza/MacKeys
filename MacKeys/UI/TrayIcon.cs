using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace MacKeys.UI;

/// <summary>The ⌘ in the notification area: white or black to match the taskbar, dimmed when paused.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private IntPtr _hIcon;

    public event Action? Clicked;

    public TrayIcon()
    {
        _icon = new Forms.NotifyIcon { Text = "MacKeys", Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right) Clicked?.Invoke();
        };
    }

    public void Update(bool enabled)
    {
        IntPtr old = _hIcon;
        _hIcon = Render(enabled, Theme.TaskbarIsDark);
        _icon.Icon = Drawing.Icon.FromHandle(_hIcon);
        _icon.Text = enabled ? "MacKeys — ativo" : "MacKeys — pausado";
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private static IntPtr Render(bool enabled, bool darkTaskbar)
    {
        using var bmp = Draw(enabled, darkTaskbar, IconSize);
        return bmp.GetHicon();
    }

    /// <summary>Size for the real screen scale (16 px at 100%, 24 px at 150%...), not the 96-DPI default.</summary>
    internal static int IconSize => GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());

    internal static Drawing.Bitmap Draw(bool enabled, bool darkTaskbar, int size)
    {
        var bmp = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            int alpha = enabled ? 255 : 100;
            var color = darkTaskbar ? Drawing.Color.FromArgb(alpha, 255, 255, 255) : Drawing.Color.FromArgb(alpha, 0, 0, 0);
            using var brush = new Drawing.SolidBrush(color);

            // The ⌘ glyph sits small inside its font box, so take its outline and scale it to fill the icon.
            using var path = new Drawing.Drawing2D.GraphicsPath();
            using var family = new Drawing.FontFamily("Segoe UI Symbol");
            path.AddString("⌘", family, (int)Drawing.FontStyle.Regular, 100, Drawing.PointF.Empty,
                Drawing.StringFormat.GenericTypographic);
            var bounds = path.GetBounds();
            float target = size * 0.94f;
            float scale = target / Math.Max(bounds.Width, bounds.Height);
            using var matrix = new Drawing.Drawing2D.Matrix();
            matrix.Translate(size / 2f, size / 2f);
            matrix.Scale(scale, scale);
            matrix.Translate(-(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2));
            path.Transform(matrix);
            g.FillPath(brush, path);
        }
        return bmp;
    }

    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
