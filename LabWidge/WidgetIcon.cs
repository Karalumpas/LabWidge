using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

/// <summary>Draws the app icon: a dark rounded square with a bolt. In the tray the bolt's colour follows the electricity price.</summary>
internal static class WidgetIcon
{
    public static readonly Color Amber = Color.FromArgb(255, 196, 61);
    public static readonly Color Neutral = Color.FromArgb(150, 165, 185);

    public static Color LevelColor(PriceLevel level) => level switch
    {
        PriceLevel.Cheap => Color.FromArgb(63, 185, 80),
        PriceLevel.Medium => Color.FromArgb(227, 179, 65),
        _ => Color.FromArgb(248, 81, 73)
    };

    public static Bitmap Render(int size, Color bolt)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size;
        var inset = size <= 16 ? 0f : s * 0.03f;
        var rect = new RectangleF(inset, inset, s - inset * 2, s - inset * 2);
        using (var bg = RoundedRect(rect, s * 0.24f))
        using (var fill = new LinearGradientBrush(rect, Color.FromArgb(38, 52, 74), Color.FromArgb(14, 20, 31), 90f))
        {
            g.FillPath(fill, bg);
            if (size >= 24)
            {
                using var edge = new Pen(Color.FromArgb(60, 255, 255, 255), Math.Max(1f, s / 64f));
                g.DrawPath(edge, bg);
            }
        }

        // Bolt (slightly larger at small sizes, so it can be seen in the tray)
        var boltRect = size <= 24 ? RectangleF.Inflate(rect, s * 0.07f, s * 0.07f) : rect;
        PointF P(float x, float y) => new(boltRect.X + x * boltRect.Width, boltRect.Y + y * boltRect.Height);
        var boltPts = new[]
        {
            P(0.60f, 0.10f), P(0.25f, 0.56f), P(0.47f, 0.56f),
            P(0.40f, 0.90f), P(0.76f, 0.42f), P(0.54f, 0.42f), P(0.63f, 0.10f)
        };
        using (var path = new GraphicsPath())
        {
            path.AddPolygon(boltPts);
            using var glow = new LinearGradientBrush(rect, Lighten(bolt, 0.35f), bolt, 90f);
            g.FillPath(glow, path);
        }

        // Small "signal" dot at the bottom right = network/system
        if (size >= 32)
        {
            var d = s * 0.14f;
            using var dot = new SolidBrush(Color.FromArgb(88, 166, 255));
            g.FillEllipse(dot, rect.Right - d * 1.9f, rect.Bottom - d * 1.9f, d, d);
        }
        return bmp;
    }

    public static Icon CreateIcon(int size, Color bolt)
    {
        using var bmp = Render(size, bolt);
        var h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    /// <summary>Writes a multi-size .ico. Small sizes as 32-bit DIB, 256 px as PNG.</summary>
    public static void WriteIco(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var images = sizes.Select(sz =>
        {
            using var bmp = Render(sz, Amber);
            return sz >= 256 ? EncodePng(bmp) : EncodeDib(bmp);
        }).ToArray();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
    }

    private static byte[] EncodePng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[] EncodeDib(Bitmap bmp)
    {
        int size = bmp.Width;
        var maskRow = ((size + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40); w.Write(size); w.Write(size * 2);      // BITMAPINFOHEADER, height incl. AND mask
        w.Write((short)1); w.Write((short)32);
        w.Write(0); w.Write(size * size * 4 + maskRow * size);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (var y = size - 1; y >= 0; y--)                  // bottom-up, BGRA
        {
            for (var x = 0; x < size; x++)
            {
                var c = bmp.GetPixel(x, y);
                w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
            }
        }
        w.Write(new byte[maskRow * size]);                   // alpha is used; empty mask
        return ms.ToArray();
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0.5f)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static Color Lighten(Color c, float amount) => Color.FromArgb(c.A,
        (int)(c.R + (255 - c.R) * amount), (int)(c.G + (255 - c.G) * amount), (int)(c.B + (255 - c.B) * amount));

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
