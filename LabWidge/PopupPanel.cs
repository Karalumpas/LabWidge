using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

/// <summary>
/// Shared base for the hand-drawn panels opened from the widget (Cloudflare, Proxmox): placement next to
/// the widget, closing with Esc or a click outside, clickable areas with tooltips, and scrolling with the mouse wheel
/// when the content is taller than the screen.
/// </summary>
internal abstract class PopupPanel : Form
{
    protected const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private readonly ToolTip _tip = new() { UseAnimation = false, UseFading = false };
    private readonly List<Hit> _hits = new();
    private readonly float _logicalWidth;
    private string? _hoverTip;
    private float _scroll;
    private float _contentHeight;

    protected readonly Palette P;
    protected readonly PanelFonts F;
    protected Point Mouse { get; private set; } = new(-1, -1);

    /// <summary>True while e.g. a confirmation dialog is showing – so the panel does not close because it loses focus.</summary>
    protected bool KeepOpen { get; set; }

    private sealed record Hit(RectangleF Rect, string? Tip, Action? Click, Action? RightClick);

    protected PopupPanel(string title, AppSettings settings, float logicalWidth)
    {
        _logicalWidth = logicalWidth;
        P = Palette.For(settings.Theme);

        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = P.Bg;
        TopMost = true;
        Icon = AppIconProvider.GetIcon();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        F = new PanelFonts(DpiScale);
    }

    protected float DpiScale => DeviceDpi / 96f;
    protected float U(float v) => v * DpiScale;

    /// <summary>Draws the content from y and returns y after the last element.</summary>
    protected abstract float RenderContent(Graphics g, float x, float y, float w);

    protected void AddHit(RectangleF rect, string? tip, Action? click = null, Action? rightClick = null) =>
        _hits.Add(new Hit(rect, tip, click, rightClick));

    /// <summary>Called when the data changed; may be called from any thread.</summary>
    protected void RequestRedraw(bool resize = true)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => { if (resize) FitSize(); Invalidate(); }));
    }

    // ---------- Window ----------

    protected void ShowNear(Rectangle widget)
    {
        FitSize();

        // To the left of the widget if there is room, otherwise to its right
        var screen = Screen.FromRectangle(widget).WorkingArea;
        var left = widget.Left - Width - (int)U(8);
        if (left < screen.Left) left = widget.Right + (int)U(8);
        if (left + Width > screen.Right) left = screen.Right - Width - (int)U(8);

        var top = Math.Min(widget.Top, screen.Bottom - Height - (int)U(8));
        Location = new Point(Math.Max(screen.Left, left), Math.Max(screen.Top, top));

        Show();

        // Focus is taken only after the click on the widget, otherwise neither Esc nor "close on losing focus" works
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            Activate();
            SetForegroundWindow(Handle);
            SetFocus(Handle);
        }));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!KeepOpen) Close();
    }

    /// <summary>Escape is caught in the message loop, since the panel has no child controls.</summary>
    protected override void WndProc(ref Message m)
    {
        const int WmKeyDown = 0x0100;
        const int WmSysKeyDown = 0x0104;

        if ((m.Msg == WmKeyDown || m.Msg == WmSysKeyDown) && (Keys)(int)m.WParam == Keys.Escape)
        {
            Close();
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            F.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW: no Alt+Tab / taskbar
            return cp;
        }
    }

    protected void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not open {url}: {ex.Message}");
        }
    }

    // ---------- Mouse ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Mouse = e.Location;
        var hit = _hits.LastOrDefault(h => h.Rect.Contains(Mouse));
        Cursor = hit?.Click != null ? Cursors.Hand : Cursors.Default;
        if (hit?.Tip != _hoverTip)
        {
            _hoverTip = hit?.Tip;
            if (_hoverTip == null) _tip.Hide(this);
            else _tip.Show(_hoverTip, this, e.X + 14, e.Y + 20, 5000);
        }
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Mouse = new Point(-1, -1);
        _hoverTip = null;
        _tip.Hide(this);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var right = e.Button == MouseButtons.Right;
        if (!right && e.Button != MouseButtons.Left) return;
        var hit = _hits.LastOrDefault(h => (right ? h.RightClick : h.Click) != null && h.Rect.Contains(e.Location));
        if (hit == null) return;
        _tip.Hide(this);
        _hoverTip = null;
        (right ? hit.RightClick : hit.Click)!();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var scroll = Math.Clamp(_scroll - e.Delta / 120f * U(66), 0, MaxScroll);
        if (scroll == _scroll) return;
        _scroll = scroll;
        _tip.Hide(this);
        _hoverTip = null;
        Invalidate();
    }

    // ---------- Drawing ----------

    private float MaxScroll => Math.Max(0, _contentHeight - ClientSize.Height);

    /// <summary>All the content, but never taller than the screen – the rest is scrolled with the mouse wheel.</summary>
    private int TargetHeight()
    {
        var max = Screen.FromControl(this).WorkingArea.Height - (int)U(16);
        return (int)Math.Ceiling(Math.Min(_contentHeight, max));
    }

    protected void FitSize()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        Render(g);
        ClientSize = new Size((int)Math.Ceiling(U(_logicalWidth)), TargetHeight());
        _scroll = Math.Min(_scroll, MaxScroll);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(P.Bg);
        Render(g);
        if (Math.Abs(ClientSize.Height - TargetHeight()) > 1)
        {
            ClientSize = new Size(ClientSize.Width, TargetHeight());
            _scroll = Math.Min(_scroll, MaxScroll);
            Invalidate();
            return;
        }

        if (MaxScroll > 0)
        {
            var track = ClientSize.Height - U(8);
            var thumb = Math.Max(U(24), track * ClientSize.Height / _contentHeight);
            var top = U(4) + (track - thumb) * (_scroll / MaxScroll);
            FillRound(g, new RectangleF(ClientSize.Width - U(7), top, U(3), thumb), Color.FromArgb(120, P.TextDim), U(1.5f));
        }

        using var border = new Pen(P.Line, Math.Max(1f, DpiScale));
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void Render(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        _hits.Clear();
        var x = U(16);
        var y = RenderContent(g, x, U(14) - _scroll, U(_logicalWidth) - x * 2);
        DrawText(g, MaxScroll > 0
                     ? L.T("Scroll for more  ·  Esc or a click outside closes", "Rul for mere  ·  Esc eller klik ved siden af lukker")
                     : L.T("Click outside – or press Esc – to close", "Klik ved siden af – eller Esc – for at lukke"),
                 F.Tiny, P.TextDim, x, y + U(6));
        _contentHeight = y + U(28) + _scroll;
    }

    // ---------- Building blocks ----------

    protected float Header(Graphics g, string glyph, string title, string right, float x, float y, float w)
    {
        DrawText(g, glyph, F.Icon, P.TextSecondary, x, y + U(1));
        DrawText(g, title, F.SmallBold, P.TextSecondary, x + U(21), y);
        DrawText(g, right, F.Small, P.TextDim, x + w, y, right: true);
        return Divider(g, x, y + U(22), w);
    }

    protected float Subheading(Graphics g, string text, float x, float y)
    {
        DrawText(g, text, F.Tiny, P.TextDim, x, y);
        return y + U(19);
    }

    protected float Divider(Graphics g, float x, float y, float w)
    {
        using var pen = new Pen(P.Line, Math.Max(1f, DpiScale));
        g.DrawLine(pen, x, y, x + w, y);
        return y + U(10);
    }

    protected float LinkRow(Graphics g, string text, string url, float x, float y, float w)
    {
        var row = new RectangleF(x - U(6), y - U(3), w + U(12), U(22));
        var hovered = row.Contains(Mouse);
        if (hovered) FillRound(g, row, P.HoverBg, U(5));
        DrawText(g, "", F.IconSmall, hovered ? P.Blue : P.TextDim, x, y + U(3));
        DrawText(g, text, F.Body, hovered ? P.Blue : P.TextSecondary, x + U(20), y);
        AddHit(row, url, () => Open(url));
        return y + U(22);
    }

    protected void Dot(Graphics g, Color color, float x, float y, float size = 8)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, new RectangleF(x, y, U(size), U(size)));
    }

    /// <summary>Text over several lines, split at spaces.</summary>
    protected float Wrapped(Graphics g, string text, Font font, Color color, float x, float y, float w)
    {
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Measure(g, candidate, font).Width > w)
            {
                DrawText(g, line, font, color, x, y);
                y += U(18);
                line = word;
            }
            else
            {
                line = candidate;
            }
        }
        if (line.Length > 0)
        {
            DrawText(g, line, font, color, x, y);
            y += U(18);
        }
        return y + U(2);
    }

    protected static string Fit(Graphics g, string text, Font font, float width)
    {
        if (Measure(g, text, font).Width <= width) return text;
        var shown = text;
        while (shown.Length > 3 && Measure(g, shown + "…", font).Width > width) shown = shown[..^1];
        return shown.TrimEnd() + "…";
    }

    protected static void FillRound(Graphics g, RectangleF r, Color c, float radius)
    {
        using var path = WidgetIcon.RoundedRect(r, radius);
        using var brush = new SolidBrush(c);
        g.FillPath(brush, path);
    }

    protected static SizeF Measure(Graphics g, string s, Font f) => TextRenderer.MeasureText(g, s, f, Size.Empty, TF);

    protected static float DrawText(Graphics g, string s, Font f, Color c, float x, float y, bool right = false)
    {
        var size = Measure(g, s, f);
        var px = right ? x - size.Width : x;
        TextRenderer.DrawText(g, s, f, new Point((int)Math.Round(px), (int)Math.Round(y)), c, TF);
        return size.Width;
    }
}

internal sealed class PanelFonts : IDisposable
{
    public readonly Font Body, BodyBold, Small, SmallBold, Tiny, Icon, IconSmall;

    public PanelFonts(float scale)
    {
        var iconFamily = HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        Body = new Font("Segoe UI", 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        BodyBold = new Font("Segoe UI Semibold", 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Small = new Font("Segoe UI", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        SmallBold = new Font("Segoe UI Semibold", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Tiny = new Font("Segoe UI", 10.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Icon = new Font(iconFamily, 13 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        IconSmall = new Font(iconFamily, 10 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static bool HasFont(string name)
    {
        using var fonts = new System.Drawing.Text.InstalledFontCollection();
        return fonts.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        foreach (var f in new[] { Body, BodyBold, Small, SmallBold, Tiny, Icon, IconSmall }) f.Dispose();
    }
}
