using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacKeys.QuickLook;

/// <summary>
/// Large thumbnails from the Windows shell: the same images Explorer shows in "Extra large icons",
/// including the ones apps install for their formats (Revit, AutoCAD, Office...).
/// </summary>
internal static class ShellThumbnail
{
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;
    private const int SIIGBF_THUMBNAILONLY = 0x8;

    /// <summary>A real thumbnail of the content if the shell has one, otherwise the file's large icon.</summary>
    public static BitmapSource? Get(string path, int size, out bool isThumbnail)
    {
        isThumbnail = true;
        var image = Get(path, size, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK);
        if (image != null) return image;
        isThumbnail = false;
        return Get(path, size, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK);
    }

    private static BitmapSource? Get(string path, int size, int flags)
    {
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            var factory = (IShellItemImageFactory)QlNative.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid);
            try
            {
                if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out IntPtr hbitmap) != 0 || hbitmap == IntPtr.Zero)
                    return null;
                try { return ToBitmapSource(hbitmap); }
                finally { QlNative.DeleteObject(hbitmap); }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Copies a 32-bit DIB keeping its alpha (Imaging.CreateBitmapSourceFromHBitmap drops it).</summary>
    private static BitmapSource? ToBitmapSource(IntPtr hbitmap)
    {
        var dib = new DIBSECTION();
        if (GetObject(hbitmap, Marshal.SizeOf<DIBSECTION>(), ref dib) == 0 || dib.dsBm.bmBits == IntPtr.Zero ||
            dib.dsBm.bmBitsPixel != 32)
        {
            var fallback = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }

        int width = dib.dsBm.bmWidth, height = dib.dsBm.bmHeight, stride = dib.dsBm.bmWidthBytes;
        var pixels = new byte[stride * height];
        Marshal.Copy(dib.dsBm.bmBits, pixels, 0, pixels.Length);

        // Photos come back with alpha = 0 everywhere; treat those as opaque.
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { hasAlpha = true; break; }

        BitmapSource source = BitmapSource.Create(width, height, 96, 96,
            hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, stride);

        // A positive height means a bottom-up DIB.
        if (dib.dsBmih.biHeight > 0)
            source = new TransformedBitmap(source, new ScaleTransform(1, -1));

        source.Freeze();
        return source;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr obj, int size, ref DIBSECTION dib);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }
}
