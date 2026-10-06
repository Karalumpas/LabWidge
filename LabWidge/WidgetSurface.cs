using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>
/// The widget is a layered window that sets its own pixels with UpdateLayeredWindow, so it can have any shape: the rounded
/// widget plus the rail's tab around the icons. The rest of the rail column is fully transparent – and clicks go through it.
/// Everything is still drawn by <see cref="Render"/>; this only turns the frame into the window's pixels.
/// </summary>
internal sealed partial class DashboardForm
{
    private const float CornerRadius = 8;

    private double _opacity = 1;
    private bool _presentQueued;
    private byte[] _frameBytes = Array.Empty<byte>(), _maskBytes = Array.Empty<byte>(), _outBytes = Array.Empty<byte>();
    private Bitmap? _mask;
    private string? _maskKey;

    protected override void OnInvalidated(InvalidateEventArgs e)
    {
        base.OnInvalidated(e);
        QueuePresent();
    }

    /// <summary>Redraws once for any number of Invalidate calls in a row.</summary>
    private void QueuePresent()
    {
        if (_presentQueued || !IsHandleCreated || IsDisposed) return;
        _presentQueued = true;
        BeginInvoke(new Action(Present));
    }

    /// <summary>Renders the widget and hands the pixels, with the shape as their transparency, to Windows.</summary>
    private void Present()
    {
        _presentQueued = false;
        if (IsDisposed || !IsHandleCreated || !Visible) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;
            // GDI text needs an opaque surface; the transparency comes from the shape afterwards
            using var frame = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            SizeF size;
            using (var g = Graphics.FromImage(frame))
            {
                g.Clear(_p.Bg);
                size = Render(g);
            }
            var height = ViewHeight(size.Height);
            if (Math.Abs(h - height) > 1 || Math.Abs(w - size.Width) > 1)
            {
                SetSize(size); // the content changed size: draw again at the new size
                continue;
            }
            if (_scroll > MaxScroll)
            {
                _scroll = MaxScroll;
                continue;
            }
            PresentFrame(frame);
            return;
        }
    }

    /// <summary>The rounded content: everything but the rail column.</summary>
    private RectangleF ContentRect(float width, float height) => new(ContentLeft, 0, width - RailWidth, height);

    /// <summary>The window's shape: the rounded content, and the drawer when the rail is docked. Everything else is transparent.</summary>
    private GraphicsPath WidgetShape(float width, float height)
    {
        var path = new GraphicsPath(FillMode.Winding);
        path.AddPath(WidgetIcon.RoundedRect(ContentRect(width, height), U(CornerRadius)), false);
        if (RailDocked)
        {
            using var tab = RailTabPath(RailLayout(height).Tab, width);
            tab.CloseFigure();
            path.AddPath(tab, false);
        }
        return path;
    }

    /// <summary>
    /// The border: the drawer's first, then the widget's on top – so the drawer looks like it comes out from behind the widget.
    /// Both lie half a pixel inside the shape, so they stay crisp.
    /// </summary>
    private void DrawOutline(Graphics g, float width, float height)
    {
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(_p.Line, Math.Max(1f, DpiScale));
        var half = pen.Width / 2;
        if (RailDocked)
        {
            var tab = RailLayout(height).Tab;
            using var outline = RailTabPath(new RectangleF(tab.X + half, tab.Y + half, tab.Width - half, tab.Height - half * 2), width);
            g.DrawPath(pen, outline);
        }
        using var content = WidgetIcon.RoundedRect(RectangleF.Inflate(ContentRect(width, height), -half, -half), U(CornerRadius) - half);
        g.DrawPath(pen, content);
        g.SmoothingMode = mode;
    }

    /// <summary>The frame with the widget's shape as its transparency: what the window shows.</summary>
    internal Bitmap Compose(Bitmap frame)
    {
        int w = frame.Width, h = frame.Height;
        var key = $"{w}x{h}|{RailDocked}|{_settings.LaunchRailPlacement}|{(RailDocked ? RailLayout(h).Tab : RectangleF.Empty)}|{DeviceDpi}";
        if (_mask == null || _maskKey != key)
        {
            _mask?.Dispose();
            _mask = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(_mask);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = WidgetShape(w, h);
            g.FillPath(Brushes.White, shape);
            _maskKey = key;
        }

        var output = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, w, h);
        var f = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var m = _mask.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var o = output.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (_frameBytes.Length < f.Stride * h) _frameBytes = new byte[f.Stride * h];
            if (_maskBytes.Length < m.Stride * h) _maskBytes = new byte[m.Stride * h];
            if (_outBytes.Length < o.Stride * h) _outBytes = new byte[o.Stride * h];
            Marshal.Copy(f.Scan0, _frameBytes, 0, f.Stride * h);
            Marshal.Copy(m.Scan0, _maskBytes, 0, m.Stride * h);
            for (var y = 0; y < h; y++)
            {
                int fi = y * f.Stride, mi = y * m.Stride, oi = y * o.Stride;
                for (var x = 0; x < w; x++, fi += 3, mi += 4, oi += 4)
                {
                    _outBytes[oi] = _frameBytes[fi];
                    _outBytes[oi + 1] = _frameBytes[fi + 1];
                    _outBytes[oi + 2] = _frameBytes[fi + 2];
                    _outBytes[oi + 3] = _maskBytes[mi + 3];
                }
            }
            Marshal.Copy(_outBytes, 0, o.Scan0, o.Stride * h);
        }
        finally
        {
            frame.UnlockBits(f);
            _mask.UnlockBits(m);
            output.UnlockBits(o);
        }

        return output;
    }

    private void PresentFrame(Bitmap frame)
    {
        using var output = Compose(frame);
        int w = output.Width, h = output.Height;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = output.GetHbitmap(Color.FromArgb(0)); // premultiplied, as UpdateLayeredWindow wants
        var old = SelectObject(memory, bitmap);
        try
        {
            var size = new NativeSize { Width = w, Height = h };
            var source = new NativePoint();
            var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = (byte)Math.Round(255 * _opacity), AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, memory, ref source, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref NativeSize psize, IntPtr hdcSrc,
        ref NativePoint pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
