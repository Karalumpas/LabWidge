using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

/// <summary>Publishes complete pixels in one update, including while a window is still hidden.</summary>
internal static class LayeredWindowSurface
{
    internal static Bitmap Round(Bitmap opaque, float radius)
    {
        var result = new Bitmap(opaque.Width, opaque.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        using (var path = WidgetIcon.RoundedRect(new RectangleF(0, 0, opaque.Width, opaque.Height), radius))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(Brushes.White, path);
        }
        var rect = new Rectangle(Point.Empty, opaque.Size);
        var source = opaque.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var target = result.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var rgb = new byte[source.Stride * source.Height];
            var rgba = new byte[target.Stride * target.Height];
            Marshal.Copy(source.Scan0, rgb, 0, rgb.Length);
            Marshal.Copy(target.Scan0, rgba, 0, rgba.Length);
            for (var y = 0; y < opaque.Height; y++)
            for (var x = 0; x < opaque.Width; x++)
            {
                var a = y * source.Stride + x * 3;
                var b = y * target.Stride + x * 4;
                rgba[b] = rgb[a]; rgba[b + 1] = rgb[a + 1]; rgba[b + 2] = rgb[a + 2];
            }
            Marshal.Copy(rgba, 0, target.Scan0, rgba.Length);
        }
        finally { opaque.UnlockBits(source); result.UnlockBits(target); }
        return result;
    }

    /// <summary>False when Windows refused the update (e.g. while the session is locked); the caller simply tries again next frame.</summary>
    internal static bool Present(IntPtr window, Bitmap pixels, double opacity)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = pixels.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memory, bitmap);
        try
        {
            var size = new NativeSize { Width = pixels.Width, Height = pixels.Height };
            var source = new NativePoint();
            var blend = new BlendFunction { SourceConstantAlpha = (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)), AlphaFormat = 1 };
            // Never throw: this runs from queued paint callbacks, where an exception would end the app
            return UpdateLayeredWindow(window, screen, IntPtr.Zero, ref size, memory, ref source, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref NativeSize psize, IntPtr hdcSrc, ref NativePoint pptSrc, int crKey, ref BlendFunction blend, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
