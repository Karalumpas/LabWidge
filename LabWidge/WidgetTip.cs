using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>
/// Explanatory text for the widget, shown beside it (left of the widget, at the height of the mouse) so it never
/// covers the content. Drawn in the widget's own colours; falls back to the right side when there is no room on the left.
/// </summary>
internal sealed class WidgetTip : Form
{
    private readonly System.Windows.Forms.Timer _hideTimer = new();
    private string _text = "";
    private Palette _p = Palette.Dark;
    private Font? _font;
    private float _scale = 1;
    private string? _fontKey;
    private Bitmap? _preparedFrame;

    private const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left;

    public WidgetTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        _hideTimer.Tick += (_, _) => HideTip();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Not Form.TopMost: it uses SetWindowPos when the handle is created and can activate the
            // tooltip before ShowWithoutActivation takes effect. Topmost follows the owner, see ShowBeside.
            cp.ExStyle |= 0x80 | 0x08000000 | 0x20 | 0x80000; // TOOLWINDOW | NOACTIVATE | TRANSPARENT | LAYERED
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Shows the text beside <paramref name="owner"/>, vertically centred on <paramref name="screenY"/>.</summary>
    public void ShowBeside(Form owner, string text, int screenY, Palette palette, double opacity, int durationMs = 0, AppSettings? appearance = null)
    {
        _hideTimer.Stop();
        var scale = owner.DeviceDpi / 96f;
        appearance ??= new AppSettings();
        var fontKey = $"{appearance.WidgetFontFamily}|{appearance.WidgetTextSize}|{appearance.WidgetBoldText}";
        if (_font == null || Math.Abs(scale - _scale) > 0.01f || _fontKey != fontKey)
        {
            _font?.Dispose();
            _font = AppearanceFonts.Create(appearance, 12f * scale);
            _fontKey = fontKey;
            _scale = scale;
        }
        _text = text;
        _p = palette;
        BackColor = _p.Bg;

        var pad = new Size((int)(10 * scale), (int)(7 * scale));
        var gap = (int)(8 * scale);
        var wa = Screen.FromControl(owner).WorkingArea;
        var maxText = Math.Max((int)(160 * scale), Math.Min((int)(340 * scale), wa.Width / 3));
        var textSize = TextRenderer.MeasureText(_text, _font, new Size(maxText, int.MaxValue), TF | TextFormatFlags.WordBreak);
        var size = new Size(textSize.Width + pad.Width * 2, textSize.Height + pad.Height * 2);

        var x = owner.Left - gap - size.Width;
        if (x < wa.Left) x = Math.Min(owner.Right + gap, wa.Right - size.Width); // no room on the left
        var y = Math.Clamp(screenY - size.Height / 2, wa.Top, Math.Max(wa.Top, wa.Bottom - size.Height));

        // Rasterise the text, background and border BEFORE making any window visible.
        // WS_EX_TRANSPARENT otherwise defers WM_PAINT until sibling windows finish painting.
        using (var opaque = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(opaque))
            {
                g.Clear(_p.Bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(pad.Width, pad.Height, size.Width - pad.Width * 2, size.Height - pad.Height * 2);
                TextRenderer.DrawText(g, _text, _font, rect, _p.TextPrimary, TF | TextFormatFlags.WordBreak);
                using var border = new Pen(_p.Line, Math.Max(1, scale));
                var half = border.Width / 2;
                using var path = WidgetIcon.RoundedRect(new RectangleF(half, half, size.Width - border.Width, size.Height - border.Width), 4 * scale);
                g.DrawPath(border, path);
            }
            var prepared = LayeredWindowSurface.Round(opaque, 4 * scale);
            _preparedFrame?.Dispose();
            _preparedFrame = prepared;
        }
        if (Owner != owner) Owner = owner;
        Bounds = new Rectangle(x, y, size.Width, size.Height);
        // As an owned window it always stays above its owner; it only goes above other programs when the owner does
        SetWindowPos(Handle, owner.TopMost ? HwndTopMost : HwndNoTopMost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        // Publish the entire tooltip atomically, then show it without activation.
        if (!LayeredWindowSurface.Present(Handle, _preparedFrame, opacity)) { HideTip(); return; }
        if (!Visible) Show(owner);
        if (durationMs > 0)
        {
            _hideTimer.Interval = durationMs;
            _hideTimer.Start();
        }
    }

    public void HideTip()
    {
        _hideTimer.Stop();
        if (Visible) Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // The layered surface is already published; this also supports bitmap previews.
        if (_preparedFrame != null) e.Graphics.DrawImageUnscaled(_preparedFrame, Point.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
            _font?.Dispose();
            _preparedFrame?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static readonly IntPtr HwndTopMost = new(-1), HwndNoTopMost = new(-2);
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

}
