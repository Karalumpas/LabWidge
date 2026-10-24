using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed partial class DashboardForm
{
    private readonly Dictionary<string, bool?> _detachedSides = new();

    private bool? DetachedHeaderSide(string? key)
    {
        if (key == null || SectionWindows.DetachedBounds(key) is not Rectangle window) return null;
        var centre = Left + Width / 2f;
        var windowCentre = window.Left + window.Width / 2f;
        if (Math.Abs(windowCentre - centre) > U(20)) return windowCentre < centre;
        var screen = Screen.FromControl(this).WorkingArea;
        return centre >= screen.Left + screen.Width / 2f;
    }

    private void OnSectionWindowChanged(string key)
    {
        if (IsDisposed || Disposing) return;
        var side = DetachedHeaderSide(key);
        if (_detachedSides.GetValueOrDefault(key) == side) return;
        _detachedSides[key] = side;
        // Only a direction/state change repaints the widget, not every pixel of a window drag.
        _surfaceDirty = true;
        Invalidate();
    }

    private void CollapseDetachedSection(string key)
    {
        if (!_settings.CollapseSectionOnDetach || SectionWindows.DetachedBounds(key) == null) return;
        // Pinned summaries collapse through their expansion override. Preserve the
        // underlying detail setting so a later click can expand the summary again.
        if (PinOf(key) == SectionPin.None || !_settings.SectionPinSummaries.Contains(key)) SetCollapsed(key, true);
        _expandedSummaries.Remove(key);
        _sectionMeasurements.Clear();
        _surfaceDirty = true;
        FitSize();
        _saveSettings();
        Invalidate();
    }

    private RectangleF _markerRect;
    private bool _markerStrongLeft;

    /// <summary>Ink for a marked header. On a light theme the gradient fades to a light background, so the text
    /// turns from white on the accent to the theme's own text colour where the accent has faded.</summary>
    private Brush MarkerInk()
    {
        var onAccent = Palette.Dark.TextPrimary;
        if (_p.IsDark || _markerRect.Width <= 0) return new SolidBrush(onAccent);
        // The accent's strength at the strong end; it fades linearly to nothing at the other end
        var strongest = Math.Clamp(_settings.DetachedSectionHighlightOpacity, 10, 100) / 100.0;
        double Background(double alpha) => Luminance(Mix(_p.Bg, _p.Blue, alpha));
        bool WhiteReadsBetter(double alpha) => Contrast(Luminance(onAccent), Background(alpha)) > Contrast(Luminance(_p.TextPrimary), Background(alpha));
        if (!WhiteReadsBetter(strongest)) return new SolidBrush(_p.TextPrimary);
        // The accent strength where white starts to read better than the dark text
        double low = 0, high = strongest;
        for (var i = 0; i < 20; i++) { var mid = (low + high) / 2; if (WhiteReadsBetter(mid)) high = mid; else low = mid; }
        var fromStrong = (float)(1 - high / strongest); // 0 = the strong end, 1 = the faded end
        var edge = _markerStrongLeft ? fromStrong : 1 - fromStrong;
        var soft = 0.02f;
        var a = Math.Clamp(edge - soft, 0.001f, 0.997f);
        var b = Math.Clamp(edge + soft, a + 0.001f, 0.999f);
        var (left, right) = _markerStrongLeft ? (onAccent, _p.TextPrimary) : (_p.TextPrimary, onAccent);
        var brush = new LinearGradientBrush(_markerRect, left, right, 0f);
        // A sharp switch: each glyph gets whichever ink contrasts most with the gradient behind it
        brush.InterpolationColors = new ColorBlend { Colors = new[] { left, left, right, right }, Positions = new[] { 0f, a, b, 1f } };
        return brush;
    }

    private static Color Mix(Color a, Color b, double fraction) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * fraction), (int)Math.Round(a.G + (b.G - a.G) * fraction), (int)Math.Round(a.B + (b.B - a.B) * fraction));

    private static double Luminance(Color c)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    private static double Contrast(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    private void DrawDetachedHeader(Graphics g, RectangleF rect, bool strongLeft)
    {
        _markerRect = rect;
        _markerStrongLeft = strongLeft;
        var alpha = (int)Math.Round(255 * Math.Clamp(_settings.DetachedSectionHighlightOpacity, 10, 100) / 100.0);
        var strong = Color.FromArgb(alpha, _p.Blue);
        var clear = Color.FromArgb(0, _p.Blue);
        using var gradient = new LinearGradientBrush(rect, strongLeft ? strong : clear, strongLeft ? clear : strong, 0f);
        using var shape = WidgetIcon.RoundedRect(rect, U(6));
        g.FillPath(gradient, shape);
    }

    // Blend glyph edges with the actual gradient rather than a solid GDI text background.
    private float DrawHeaderText(Graphics g, bool marked, string text, Font font, Color colour,
        float x, float y, bool right = false)
    {
        if (!marked) return DrawText(g, text, font, colour, x, y, right);
        var hint = g.TextRenderingHint;
        try
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var brush = MarkerInk();
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.NoWrap;
            var width = g.MeasureString(text, font, PointF.Empty, format).Width;
            var px = right ? x - width : x;
            g.DrawString(text, font, brush, new PointF((float)Math.Round(px), (float)Math.Round(y)), format);
            return width;
        }
        finally { g.TextRenderingHint = hint; }
    }

    private void ExpandReturnedSection(string key)
    {
        // Only undo what detaching did: with automatic collapse off, the user's own collapsed state stays
        if (IsDisposed || Disposing || !_settings.CollapseSectionOnDetach) return;
        SetCollapsed(key, false);
        if (PinOf(key) != SectionPin.None && _settings.SectionPinSummaries.Contains(key))
            _expandedSummaries.Add(key);
        _sectionMeasurements.Clear();
        _surfaceDirty = true;
        FitSize();
        _saveSettings();
        Invalidate();
    }
}
