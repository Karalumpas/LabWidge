using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>
/// A Windows 11 style on/off switch: the text on the left, the switch on the right, filling the card's width.
/// It is still a CheckBox, so the pages use Checked and CheckedChanged as before.
/// </summary>
internal sealed class ToggleSwitch : CheckBox
{
    private bool _hover;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Width = Ui.ContentWidth;
        Cursor = Cursors.Hand;
        UpdateHeight();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        UpdateHeight();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateHeight();
    }

    private void UpdateHeight()
    {
        var text = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(60, Width - 64), 0), TextFormatFlags.WordBreak);
        Height = Math.Max(32, text.Height + 12);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Ui.Palette;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var track = new RectangleF(Width - 44, (Height - 22) / 2f, 40, 22);
        var textRect = new Rectangle(0, 0, (int)track.X - 12, Height);
        var textColor = Enabled ? p.TextPrimary : p.TextDim;
        TextRenderer.DrawText(g, Text, Font, textRect, textColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left);

        var on = Checked;
        var alpha = Enabled ? 255 : 110;
        using (var path = WidgetIcon.RoundedRect(track, track.Height / 2))
        {
            using var fill = new SolidBrush(Color.FromArgb(alpha, on ? p.Blue : _hover && Enabled ? p.HoverBg : p.Bg));
            g.FillPath(fill, path);
            if (!on)
            {
                using var border = new Pen(Color.FromArgb(alpha, p.TextDim), 1.2f);
                g.DrawPath(border, path);
            }
        }
        var size = on ? 14f : 12f;
        var knob = new RectangleF(on ? track.Right - size - 4 : track.X + 5, track.Y + (track.Height - size) / 2, size, size);
        using var brush = new SolidBrush(Color.FromArgb(alpha, on ? Color.White : p.TextSecondary));
        g.FillEllipse(brush, knob);

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(p.Blue, 1.5f);
            g.DrawRectangle(focus, track.X - 3, track.Y - 3, track.Width + 6, track.Height + 6);
        }
    }
}

/// <summary>A rounded card that groups the settings of one section; its first control is the section title.</summary>
internal sealed class Card : FlowLayoutPanel
{
    public const int Inset = 16;

    public Card()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(Inset, 12, Inset, 12);
        Margin = new Padding(0, 6, 0, 8);
        MinimumSize = new Size(Ui.ContentWidth + Inset * 2, 0);
        MaximumSize = new Size(Ui.ContentWidth + Inset * 2, 0);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var p = Ui.Palette;
        e.Graphics.Clear(Parent?.BackColor ?? p.Bg);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = WidgetIcon.RoundedRect(r, 8);
        using var fill = new SolidBrush(BackColor);
        e.Graphics.FillPath(fill, path);
        using var border = new Pen(p.Line);
        e.Graphics.DrawPath(border, path);
    }
}

/// <summary>Colours the settings and the setup guide in the widget's light or dark theme.</summary>
internal static class SettingsTheme
{
    /// <summary>The colour of the cards – a step lighter than the window in the dark theme, white in the light theme.</summary>
    public static Color CardColor(Palette p) => p.IsDark ? Color.FromArgb(27, 32, 41) : Color.White;

    /// <summary>The window behind the cards.</summary>
    public static Color PageColor(Palette p) => p.IsDark ? p.Bg : Color.FromArgb(243, 245, 248);

    public static void Apply(Control root, Palette p)
    {
        foreach (Control c in root.Controls) Style(c, p);
    }

    private static void Style(Control c, Palette p)
    {
        switch (c)
        {
            case Card:
                c.BackColor = CardColor(p);
                c.ForeColor = p.TextPrimary;
                break;
            case TextBox t:
                t.BorderStyle = BorderStyle.FixedSingle;
                t.BackColor = p.IsDark ? Color.FromArgb(34, 40, 50) : Color.White;
                t.ForeColor = p.TextPrimary;
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = p.IsDark ? Color.FromArgb(34, 40, 50) : Color.White;
                cb.ForeColor = p.TextPrimary;
                break;
            case NumericUpDown n:
                n.BorderStyle = BorderStyle.FixedSingle;
                n.BackColor = p.IsDark ? Color.FromArgb(34, 40, 50) : Color.White;
                n.ForeColor = p.TextPrimary;
                break;
            case Button b when b.Tag as string != "accent":
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = p.Line;
                b.FlatAppearance.MouseOverBackColor = p.HoverBg;
                b.BackColor = p.IsDark ? Color.FromArgb(38, 45, 56) : Color.FromArgb(246, 248, 250);
                b.ForeColor = p.TextPrimary;
                b.Cursor = Cursors.Hand;
                break;
            case Button accent:
                accent.FlatStyle = FlatStyle.Flat;
                accent.FlatAppearance.BorderSize = 0;
                accent.BackColor = p.Blue;
                accent.ForeColor = Color.White;
                accent.Cursor = Cursors.Hand;
                break;
            case LinkLabel l:
                l.LinkColor = p.Blue;
                l.ActiveLinkColor = p.Blue;
                l.VisitedLinkColor = p.Blue;
                break;
            case CheckedListBox list:
                list.BorderStyle = BorderStyle.FixedSingle;
                list.BackColor = p.IsDark ? Color.FromArgb(34, 40, 50) : Color.White;
                list.ForeColor = p.TextPrimary;
                break;
            case DataGridView grid:
                grid.BackgroundColor = p.IsDark ? Color.FromArgb(34, 40, 50) : Color.White;
                grid.GridColor = p.Line;
                grid.BorderStyle = BorderStyle.FixedSingle;
                grid.EnableHeadersVisualStyles = false;
                grid.DefaultCellStyle.BackColor = grid.BackgroundColor;
                grid.DefaultCellStyle.ForeColor = p.TextPrimary;
                grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(p.IsDark ? 70 : 40, p.Blue);
                grid.DefaultCellStyle.SelectionForeColor = p.TextPrimary;
                grid.ColumnHeadersDefaultCellStyle.BackColor = p.IsDark ? Color.FromArgb(38, 45, 56) : Color.FromArgb(246, 248, 250);
                grid.ColumnHeadersDefaultCellStyle.ForeColor = p.TextSecondary;
                grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
                break;
        }
        foreach (Control child in c.Controls) Style(child, p);
    }

    /// <summary>A dark title bar for a dark window (Windows 10 2004 and newer).</summary>
    public static void TitleBar(Form form, Palette p)
    {
        if (!form.IsHandleCreated) return;
        try
        {
            var dark = p.IsDark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch
        {
            // Older Windows: the normal title bar
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
