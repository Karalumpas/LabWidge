using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

/// <summary>
/// An offscreen bitmap that GDI can draw on directly, like a window: a DIB section in its own memory DC.
/// TextRenderer (GDI) needs this. Drawn through Graphics.FromImage instead, ClearType blends the glyph edges with black
/// rather than with the background, so dark text on a light background comes out too dark, thick and smeared.
/// <see cref="Bitmap"/> shows the same pixels to GDI+ for reading; it stays valid until the surface is disposed.
/// </summary>
internal sealed class GdiSurface : IDisposable
{
    private readonly IntPtr _dc, _dib, _previous;
    private readonly Bitmap _bitmap;
    private bool _disposed;

    internal GdiSurface(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, // negative: top-down rows, like a GDI+ Bitmap
            Planes = 1, BitCount = 32
        };
        _dc = CreateCompatibleDC(IntPtr.Zero);
        if (_dc == IntPtr.Zero) throw new Win32Exception();
        _dib = CreateDIBSection(_dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero)
        {
            var error = new Win32Exception();
            DeleteDC(_dc);
            throw error;
        }
        _previous = SelectObject(_dc, _dib);
        _bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, bits);
    }

    /// <summary>
    /// The surface's pixels (the fourth byte is unused). Dispose the Graphics drawing on it first;
    /// GDI may still hold batched calls, so they are flushed here.
    /// </summary>
    internal Bitmap Bitmap
    {
        get
        {
            GdiFlush();
            return _bitmap;
        }
    }

    internal Size Size => _bitmap.Size;

    /// <summary>Draws on the surface through its DC, so GDI text blends with what is already there.</summary>
    internal Graphics CreateGraphics() => Graphics.FromHdc(_dc);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bitmap.Dispose();
        SelectObject(_dc, _previous);
        DeleteObject(_dib);
        DeleteDC(_dc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
