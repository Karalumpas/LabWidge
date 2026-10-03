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

    private const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left;

    public WidgetTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        _hideTimer.Tick += (_, _) => HideTip();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000 | 0x20; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT (clicks pass through)
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var round = 3; // DWMWCP_ROUNDSMALL
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch
        {
            // Older Windows: square corners.
        }
    }

    /// <summary>Shows the text beside <paramref name="owner"/>, vertically centred on <paramref name="screenY"/>.</summary>
    public void ShowBeside(Form owner, string text, int screenY, Palette palette, double opacity, int durationMs = 0)
    {
        _hideTimer.Stop();
        var scale = owner.DeviceDpi / 96f;
        if (_font == null || Math.Abs(scale - _scale) > 0.01f)
        {
            _font?.Dispose();
            _font = new Font("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _scale = scale;
        }
        _text = text;
        _p = palette;
        BackColor = _p.Bg;
        Opacity = opacity;
        ApplyBorderColor();

        var pad = new Size((int)(10 * scale), (int)(7 * scale));
        var gap = (int)(8 * scale);
        var wa = Screen.FromControl(owner).WorkingArea;
        var maxText = Math.Max((int)(160 * scale), Math.Min((int)(340 * scale), wa.Width / 3));
        var textSize = TextRenderer.MeasureText(_text, _font, new Size(maxText, int.MaxValue), TF | TextFormatFlags.WordBreak);
        var size = new Size(textSize.Width + pad.Width * 2, textSize.Height + pad.Height * 2);

        var x = owner.Left - gap - size.Width;
        if (x < wa.Left) x = Math.Min(owner.Right + gap, wa.Right - size.Width); // no room on the left
        var y = Math.Clamp(screenY - size.Height / 2, wa.Top, Math.Max(wa.Top, wa.Bottom - size.Height));

        Bounds = new Rectangle(x, y, size.Width, size.Height);
        if (!Visible) Show(owner);
        Invalidate();
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

    private void ApplyBorderColor()
    {
        if (!IsHandleCreated) CreateHandle();
        try
        {
            var border = _p.Line.R | (_p.Line.G << 8) | (_p.Line.B << 16);
            DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
        }
        catch
        {
            // Not supported.
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_p.Bg);
        if (_font == null) return;
        var pad = new Size((int)(10 * _scale), (int)(7 * _scale));
        var rect = new Rectangle(pad.Width, pad.Height, ClientSize.Width - pad.Width * 2, ClientSize.Height - pad.Height * 2);
        TextRenderer.DrawText(g, _text, _font, rect, _p.TextPrimary, TF | TextFormatFlags.WordBreak);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
            _font?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
