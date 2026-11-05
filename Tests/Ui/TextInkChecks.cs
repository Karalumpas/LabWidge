using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

internal static partial class UiChecks
{
    /// <summary>
    /// GDI text on the widget's surface must blend with the background, as in a window. Drawn through
    /// Graphics.FromImage, ClearType blends with black instead: grey text on the light theme came out
    /// nearly black and smeared. No text pixel may be darker than the text colour itself.
    /// </summary>
    private static void TextInkChecks()
    {
        var palette = Palette.Light;
        using var font = new Font("Segoe UI Semibold", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        const string text = "ELPRIS · DK1 spotpris, inkl. moms";
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        using var surface = new GdiSurface(260, 24);
        foreach (var hint in new[] { TextRenderingHint.SystemDefault, TextRenderingHint.ClearTypeGridFit, TextRenderingHint.AntiAliasGridFit })
        {
            using (var g = surface.CreateGraphics())
            {
                g.TextRenderingHint = hint;
                g.Clear(palette.Bg);
                TextRenderer.DrawText(g, text, font, new Point(4, 4), palette.TextSecondary, flags);
            }
            var bitmap = surface.Bitmap;
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            var bytes = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            bitmap.UnlockBits(data);
            static int Luma(int r, int g, int b) => (r * 299 + g * 587 + b * 114) / 1000;
            var ink = Luma(palette.TextSecondary.R, palette.TextSecondary.G, palette.TextSecondary.B);
            var darkest = 255;
            var touched = 0;
            for (var i = 0; i < bytes.Length; i += 4)
            {
                var luma = Luma(bytes[i + 2], bytes[i + 1], bytes[i]);
                darkest = Math.Min(darkest, luma);
                if (luma < Luma(palette.Bg.R, palette.Bg.G, palette.Bg.B) - 8) touched++;
            }
            Check(touched > 50, $"Text was not drawn on the GDI surface ({hint}).");
            // A little room for ClearType's colour fringes; the broken path went ~70 levels darker than the ink.
            Check(darkest >= ink - 20, $"Light-theme text is darker than its colour ({hint}): darkest {darkest}, ink {ink}.");
        }
        Console.WriteLine("PASS Text ink: GDI text on the widget surface blends with the light background instead of black");
    }
}
