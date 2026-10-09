using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

/// <summary>
/// Shared base for the windows a widget section opens (electricity price, system, network, audio, Home Assistant,
/// Cloudflare, Proxmox), drawn in the widget's own style.
/// - A fixed header with the section's icon and title, a status, a pin and a close button.
/// - Drag the header to move the window, and an edge or corner to resize it.
/// - Not pinned: closes with Esc or a click outside, like a pop-up. Pinned: stays open, and position and size are
///   remembered – also across restarts.
/// - Content taller than the window is scrolled with the mouse wheel. A control (e.g. a WebView) can fill the window instead.
/// </summary>
internal abstract class PopupPanel : Form
{
    protected const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    private const float HeaderHeight = 38;
    private const float Edge = 6;

    private readonly WidgetTip _tip = new();
    private readonly List<Hit> _hits = new();
    private readonly List<Hit> _chromeHits = new();
    private readonly float _logicalWidth;
    private readonly string _glyph;
    private readonly string _title;
    private readonly Action _saveSettings;
    private string? _hoverTip;
    private float _scroll;
    private float _contentHeight;
    private Control? _hosted;
    private bool _closingForShutdown;

    private readonly AppSettings _initialSettings;

    /// <summary>The current settings – the object is replaced when the user saves the settings window.</summary>
    protected AppSettings Settings => SectionWindows.Settings ?? _initialSettings;
    protected Palette P { get; private set; }
    protected readonly PanelFonts F;
    protected Point Mouse { get; private set; } = new(-1, -1);

    /// <summary>The section key, e.g. "price" – also the key of the stored window state.</summary>
    public string Key { get; }

    /// <summary>True while e.g. a confirmation dialog is showing – so the panel does not close because it loses focus.</summary>
    protected bool KeepOpen { get; set; }

    private sealed record Hit(RectangleF Rect, string? Tip, Action? Click, Action? RightClick);

    protected PopupPanel(string key, string glyph, string title, AppSettings settings, Action saveSettings, float logicalWidth)
    {
        Key = key;
        _glyph = glyph;
        _title = title;
        _logicalWidth = logicalWidth;
        _saveSettings = saveSettings;
        _initialSettings = settings;
        P = Palette.For(settings.Theme);

        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = P.Bg;
        Icon = AppIconProvider.GetIcon();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        F = new PanelFonts(DpiScale);
        MinimumSize = new Size((int)U(260), (int)U(140));
        // The header acts as a title bar; without this a double-click on it would maximize the window
        MaximizeBox = false;
        MinimizeBox = false;
        ApplyTopMost();
    }

    protected float DpiScale => DeviceDpi / 96f;
    protected float U(float v) => v * DpiScale;

    /// <summary>The window's stored state; created on first use.</summary>
    protected SectionWindowState State
    {
        get
        {
            if (!Settings.SectionWindows.TryGetValue(Key, out var state))
                Settings.SectionWindows[Key] = state = new SectionWindowState();
            return state;
        }
    }

    public bool Pinned => State.Pinned;

    /// <summary>Text on the right of the header, e.g. "updated 12:04".</summary>
    protected virtual string? HeaderStatus => null;
    protected virtual Color? HeaderStatusColor => null;

    /// <summary>Draws the content from y and returns y after the last element. Not called while a control is hosted.</summary>
    protected abstract float RenderContent(Graphics g, float x, float y, float w);

    /// <summary>The size of a window that hosts a control, before the user has resized it.</summary>
    protected virtual Size HostedDefaultSize => new((int)U(_logicalWidth), (int)U(560));

    protected void AddHit(RectangleF rect, string? tip, Action? click = null, Action? rightClick = null) =>
        _hits.Add(new Hit(rect, tip, click, rightClick));

    /// <summary>Called when the data changed; may be called from any thread.</summary>
    protected void RequestRedraw(bool resize = true)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return; // closed while the call waited
                if (resize) FitSize();
                Invalidate();
            }));
        }
        catch (ObjectDisposedException)
        {
            // Closed in the meantime
        }
        catch (InvalidOperationException)
        {
            // The handle is being destroyed
        }
    }

    /// <summary>Fills the window below the header with a control (e.g. a WebView).</summary>
    protected void Host(Control control)
    {
        _hosted = control;
        // The edges stay free, so the window can still be resized around the control
        var edge = (int)U(Edge);
        Padding = new Padding(edge, (int)U(HeaderHeight), edge, edge);
        control.Dock = DockStyle.Fill;
        Controls.Add(control);
    }

    // ---------- Showing ----------

    /// <summary>
    /// Shows the window. A pinned window opens where it was last; otherwise it opens next to <paramref name="near"/> (the widget).
    /// </summary>
    public void ShowNear(Rectangle near)
    {
        FitSize();
        if (State.Pinned && State.Left is int l && State.Top is int t && OnScreen(new Rectangle(l, t, Width, Height)))
        {
            Location = new Point(l, t);
        }
        else
        {
            // To the left of the widget if there is room, otherwise to its right
            var screen = Screen.FromRectangle(near).WorkingArea;
            var left = near.Left - Width - (int)U(8);
            if (left < screen.Left) left = near.Right + (int)U(8);
            if (left + Width > screen.Right) left = screen.Right - Width - (int)U(8);
            var top = Math.Min(near.Top, screen.Bottom - Height - (int)U(8));
            Location = new Point(Math.Max(screen.Left, left), Math.Max(screen.Top, top));
        }
        ShowAndFocus();
    }

    /// <summary>Opens the window pinned with its header under <paramref name="screen"/> – a section dragged out of the widget.</summary>
    public void ShowPinnedAt(Point screen)
    {
        State.Pinned = true;
        ApplyTopMost();
        FitSize();
        var area = Screen.FromPoint(screen).WorkingArea;
        var x = Math.Clamp(screen.X - (int)U(40), area.Left, Math.Max(area.Left, area.Right - Width));
        var y = Math.Clamp(screen.Y - (int)U(HeaderHeight / 2), area.Top, Math.Max(area.Top, area.Bottom - Height));
        Location = new Point(x, y);
        RememberBounds();
        ShowAndFocus();
    }

    /// <summary>Opens a pinned window where it was when LabWidge closed, without taking focus.</summary>
    public void ShowRestored()
    {
        FitSize();
        if (State.Left is int l && State.Top is int t && OnScreen(new Rectangle(l, t, Width, Height)))
            Location = new Point(l, t);
        else
            Location = Screen.PrimaryScreen!.WorkingArea.Location + new Size((int)U(40), (int)U(40));
        Show();
    }

    private DateTime _shownAt;

    private void ShowAndFocus()
    {
        State.Open = State.Pinned;
        _shownAt = DateTime.Now;
        Show();
        if (IsDisposed) return; // closed while it was shown
        if (State.Pinned) _saveSettings();

        // Focus is taken only after the click on the widget, otherwise neither Esc nor "close on losing focus" works
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            Activate();
            SetForegroundWindow(Handle);
            if (_hosted == null) SetFocus(Handle);
        }));
    }

    private static bool OnScreen(Rectangle r) =>
        Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(r.Left + 20, r.Top + 4, Math.Max(1, r.Width - 40), 24)));

    /// <summary>Closes the window when LabWidge exits, keeping "open" so a pinned window comes back at the next start.</summary>
    public void CloseForShutdown()
    {
        _closingForShutdown = true;
        Close();
    }

    public void ApplySettings(AppSettings settings)
    {
        P = Palette.For(settings.Theme);
        BackColor = P.Bg;
        if (_hosted != null) _hosted.BackColor = P.Bg;
        ApplyTopMost();
        FitSize();
        Invalidate();
    }

    /// <summary>A pop-up is always on top; a pinned window follows the widget's "always on top" setting.</summary>
    private void ApplyTopMost() => TopMost = !State.Pinned || Settings.WidgetTopMost;

    private void TogglePin()
    {
        State.Pinned = !State.Pinned;
        State.Open = State.Pinned;
        ApplyTopMost();
        if (State.Pinned) RememberBounds();
        _saveSettings();
        Invalidate();
    }

    private void RememberBounds()
    {
        State.Left = Left;
        State.Top = Top;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // The click on the widget that opened the window can briefly take focus back; that must not close it again
        if ((DateTime.Now - _shownAt).TotalMilliseconds < 400) return;
        if (!KeepOpen && !State.Pinned) Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (!_closingForShutdown)
        {
            State.Open = false;
            _saveSettings();
        }
        base.OnFormClosed(e);
    }

    private const int WmNcHitTest = 0x0084, WmExitSizeMove = 0x0232, WmKeyDown = 0x0100, WmSysKeyDown = 0x0104;

    protected override void WndProc(ref Message m)
    {
        // Escape is caught in the message loop, since a drawn panel has no child controls
        if ((m.Msg == WmKeyDown || m.Msg == WmSysKeyDown) && (Keys)(int)m.WParam == Keys.Escape)
        {
            Close();
            return;
        }
        if (m.Msg == WmNcHitTest)
        {
            var screen = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
            var hit = HitTest(PointToClient(screen));
            if (hit != 1)
            {
                m.Result = (IntPtr)hit;
                return;
            }
        }
        base.WndProc(ref m);
        if (m.Msg == WmExitSizeMove)
        {
            // Moved or resized: remember it. A resized window keeps its size instead of following the content.
            RememberBounds();
            var auto = FitContentSize();
            if (Math.Abs(Width - auto.Width) > 2 || Math.Abs(Height - auto.Height) > 2 || State.Width != null)
            {
                State.Width = Width;
                State.Height = Height;
            }
            _saveSettings();
        }
    }

    /// <summary>Edges resize, the header moves the window, everything else is the client area.</summary>
    private int HitTest(Point p)
    {
        const int Client = 1, Caption = 2, Left = 10, Right = 11, Top = 12, TopLeft = 13, TopRight = 14, Bottom = 15, BottomLeft = 16, BottomRight = 17;
        var e = U(Edge);
        bool l = p.X < e, r = p.X >= ClientSize.Width - e, t = p.Y < e, b = p.Y >= ClientSize.Height - e;
        if (t && l) return TopLeft;
        if (t && r) return TopRight;
        if (b && l) return BottomLeft;
        if (b && r) return BottomRight;
        if (l) return Left;
        if (r) return Right;
        if (t) return Top;
        if (b) return Bottom;
        if (p.Y < U(HeaderHeight) && !_chromeHits.Any(h => h.Rect.Contains(p))) return Caption;
        return Client;
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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var round = 2; // DWMWCP_ROUND – the same corners as the widget
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch
        {
            // Older Windows: square corners.
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            if (!State.Pinned) Close();
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not open {url}: {ex.Message}");
        }
    }

    // ---------- Mouse ----------

    private Hit? HitAt(Point p) => _chromeHits.LastOrDefault(h => h.Rect.Contains(p)) ?? (p.Y >= U(HeaderHeight) ? _hits.LastOrDefault(h => h.Rect.Contains(p)) : null);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Mouse = e.Location;
        var hit = HitAt(Mouse);
        Cursor = hit?.Click != null ? Cursors.Hand : Cursors.Default;
        if (hit?.Tip != _hoverTip)
        {
            _hoverTip = hit?.Tip;
            if (_hoverTip == null) _tip.HideTip();
            else _tip.ShowBeside(this, _hoverTip, Cursor.Position.Y, P, 1.0);
        }
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Mouse = new Point(-1, -1);
        _hoverTip = null;
        _tip.HideTip();
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var right = e.Button == MouseButtons.Right;
        if (!right && e.Button != MouseButtons.Left) return;
        var hit = _chromeHits.LastOrDefault(h => h.Click != null && h.Rect.Contains(e.Location))
                  ?? (e.Y >= U(HeaderHeight) ? _hits.LastOrDefault(h => (right ? h.RightClick : h.Click) != null && h.Rect.Contains(e.Location)) : null);
        if (hit == null) return;
        _tip.HideTip();
        _hoverTip = null;
        (right ? hit.RightClick ?? hit.Click : hit.Click)?.Invoke();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_hosted != null) return;
        var scroll = Math.Clamp(_scroll - e.Delta / 120f * U(66), 0, MaxScroll);
        if (scroll == _scroll) return;
        _scroll = scroll;
        _tip.HideTip();
        _hoverTip = null;
        Invalidate();
    }

    // ---------- Size ----------

    private float ViewHeight => ClientSize.Height - U(HeaderHeight);
    private float MaxScroll => Math.Max(0, _contentHeight - ViewHeight);

    /// <summary>The size that fits the content (never taller than the screen) – used until the user resizes the window.</summary>
    private Size FitContentSize()
    {
        if (_hosted != null) return HostedDefaultSize;
        var max = Screen.FromControl(this).WorkingArea.Height - (int)U(16);
        return new Size((int)Math.Ceiling(U(_logicalWidth)), (int)Math.Ceiling(Math.Min(U(HeaderHeight) + _contentHeight, max)));
    }

    protected void FitSize()
    {
        if (IsDisposed) return;
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
        {
            Measure(g);
        }
        var auto = FitContentSize();
        var size = State.Width is int w && State.Height is int h ? new Size(w, h) : auto;
        var area = Screen.FromControl(this).WorkingArea;
        size = new Size(Math.Min(size.Width, area.Width), Math.Min(size.Height, area.Height));
        if (ClientSize != size) ClientSize = size;
        _scroll = Math.Min(_scroll, MaxScroll);
    }

    /// <summary>Lets the content follow its own height again (e.g. after the user resized the window).</summary>
    private void ResetSize()
    {
        State.Width = State.Height = null;
        _saveSettings();
        FitSize();
        Invalidate();
    }

    private void Measure(Graphics g)
    {
        if (_hosted != null) return;
        var saved = _hits.ToList();
        var w = ClientSize.Width > 0 && State.Width != null ? ClientSize.Width : U(_logicalWidth);
        var x = U(16);
        _hits.Clear();
        var bottom = RenderContent(g, x, 0, w - x * 2);
        _contentHeight = bottom + U(8) + U(FooterHeight);
        _hits.Clear();
        _hits.AddRange(saved);
    }

    // ---------- Drawing ----------

    private const float FooterHeight = 30;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(P.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        if (_hosted == null)
        {
            // The content scrolls below the fixed header
            var state = g.Save();
            g.SetClip(new RectangleF(0, U(HeaderHeight), ClientSize.Width, ViewHeight));
            _hits.Clear();
            var x = U(16);
            var top = U(HeaderHeight) + U(8) - _scroll;
            var y = RenderContent(g, x, top, ClientSize.Width - x * 2);
            DrawText(g, State.Pinned
                         ? L.T("Pinned  ·  drag the header to move, an edge to resize", "Fastgjort  ·  træk i overskriften for at flytte, i en kant for at ændre størrelse")
                         : MaxScroll > 0
                             ? L.T("Scroll for more  ·  Esc or a click outside closes", "Rul for mere  ·  Esc eller klik ved siden af lukker")
                             : L.T("Esc or a click outside closes  ·  pin to keep it open", "Esc eller klik ved siden af lukker  ·  fastgør for at beholde"),
                     F.Tiny, P.TextDim, x, y + U(6));
            var content = y - top + U(8) + U(FooterHeight);
            if (Math.Abs(content - _contentHeight) > 1)
            {
                _contentHeight = content;
                if (State.Width == null) BeginInvoke(new Action(() => { if (IsDisposed) return; FitSize(); Invalidate(); }));
            }
            g.Restore(state);

            if (MaxScroll > 0)
            {
                var track = ViewHeight - U(8);
                var thumb = Math.Max(U(24), track * ViewHeight / _contentHeight);
                var thumbTop = U(HeaderHeight) + U(4) + (track - thumb) * (_scroll / MaxScroll);
                FillRound(g, new RectangleF(ClientSize.Width - U(7), thumbTop, U(3), thumb), Color.FromArgb(120, P.TextDim), U(1.5f));
            }
        }

        DrawHeader(g);

        // A grip in the corner shows that the window can be resized
        using (var grip = new Pen(P.TextDim, Math.Max(1, DpiScale)))
        {
            for (var i = 0; i < 3; i++)
                g.DrawLine(grip, ClientSize.Width - U(5 + i * 4), ClientSize.Height - U(4), ClientSize.Width - U(4), ClientSize.Height - U(5 + i * 4));
        }
        using var border = new Pen(P.Line, Math.Max(1f, DpiScale));
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void DrawHeader(Graphics g)
    {
        _chromeHits.Clear();
        var h = U(HeaderHeight);
        using (var bg = new SolidBrush(P.Bg)) g.FillRectangle(bg, 0, 0, ClientSize.Width, h);
        var x = U(16);
        var ty = (h - Measure(g, "Ag", F.SmallBold).Height) / 2;
        DrawText(g, _glyph, F.Icon, P.TextSecondary, x, ty + U(1));
        DrawText(g, _title, F.SmallBold, P.TextSecondary, x + U(21), ty);

        // Close and pin on the right
        var size = U(26);
        var close = new RectangleF(ClientSize.Width - U(8) - size, (h - size) / 2, size, size);
        var pin = new RectangleF(close.X - size - U(2), close.Y, size, size);
        ChromeButton(g, close, "", P.TextSecondary, L.T("Close (Esc)", "Luk (Esc)"), Close);
        ChromeButton(g, pin, State.Pinned ? "" : "", State.Pinned ? P.Blue : P.TextDim,
            State.Pinned ? L.T("Pinned – stays open and opens again when LabWidge starts. Click to unpin.",
                               "Fastgjort – bliver åben og åbner igen, når LabWidge starter. Klik for at frigøre.")
                         : L.T("Pin – keep the window open and remember where it is", "Fastgør – hold vinduet åbent og husk, hvor det er"),
            TogglePin);
        if (State.Width != null)
        {
            var reset = new RectangleF(pin.X - size - U(2), close.Y, size, size);
            ChromeButton(g, reset, "", P.TextDim, L.T("Fit the window to its content", "Tilpas vinduet til indholdet"), ResetSize);
            pin = reset;
        }

        if (HeaderStatus is { } status)
        {
            var room = pin.X - U(10) - (x + U(21) + Measure(g, _title, F.SmallBold).Width + U(12));
            if (room > U(30))
                DrawText(g, Fit(g, status, F.Small, room), F.Small, HeaderStatusColor ?? P.TextDim, pin.X - U(8), ty + U(1), right: true);
        }

        using var line = new Pen(P.Line, Math.Max(1f, DpiScale));
        g.DrawLine(line, U(1), h - U(1), ClientSize.Width - U(1), h - U(1));
    }

    private void ChromeButton(Graphics g, RectangleF r, string glyph, Color color, string tip, Action click)
    {
        if (r.Contains(Mouse)) FillRound(g, r, P.HoverBg, U(5));
        var s = Measure(g, glyph, F.Icon);
        DrawText(g, glyph, F.Icon, r.Contains(Mouse) ? P.TextPrimary : color, r.X + (r.Width - s.Width) / 2, r.Y + (r.Height - s.Height) / 2);
        _chromeHits.Add(new Hit(r, tip, click, null));
    }

    // ---------- Building blocks ----------

    /// <summary>A small heading inside the content, e.g. "TUNNELS".</summary>
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

    /// <summary>A rounded progress bar; the colour turns amber over 80 % and red over 90 % unless given.</summary>
    protected void Bar(Graphics g, RectangleF r, double frac, Color? color = null)
    {
        if (r.Width <= r.Height) return; // too narrow window – nothing sensible to draw
        FillRound(g, r, P.Track, r.Height / 2);
        var c = color ?? (frac > 0.9 ? P.Red : frac > 0.8 ? P.Amber : P.Blue);
        FillRound(g, new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * (float)Math.Clamp(frac, 0, 1)), r.Height), c, r.Height / 2);
    }

    /// <summary>A line chart of values with a soft fill, scaled to <paramref name="max"/>.</summary>
    protected void LineChart(Graphics g, RectangleF r, IReadOnlyList<double> values, double max, Color color, bool fill = true)
    {
        FillRound(g, r, Color.FromArgb(P.IsDark ? 40 : 60, P.Track), U(4));
        if (values.Count < 2 || max <= 0) return;
        var points = values.Select((v, i) => new PointF(r.X + r.Width * i / (values.Count - 1),
            r.Bottom - (float)Math.Clamp(v / max, 0, 1) * (r.Height - U(2)) - U(1))).ToArray();
        if (fill)
        {
            using var path = new GraphicsPath();
            path.AddLines(points);
            path.AddLine(points[^1], new PointF(r.Right, r.Bottom));
            path.AddLine(new PointF(r.Right, r.Bottom), new PointF(r.X, r.Bottom));
            path.CloseFigure();
            using var brush = new SolidBrush(Color.FromArgb(46, color));
            g.FillPath(brush, path);
        }
        using var pen = new Pen(color, Math.Max(1f, U(1.4f))) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, points);
    }

    /// <summary>Label on the left, value on the right – one line.</summary>
    protected float KeyValue(Graphics g, string key, string value, float x, float y, float w, Color? color = null, string? tip = null)
    {
        DrawText(g, key, F.Body, P.TextSecondary, x, y);
        var vw = DrawText(g, Fit(g, value, F.BodyBold, w * 0.62f), F.BodyBold, color ?? P.TextPrimary, x + w, y, right: true);
        if (tip != null) AddHit(new RectangleF(x + w - vw, y - U(2), vw, U(20)), tip);
        return y + U(21);
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
        TextRenderer.DrawText(g, s, f, new Point((int)Math.Round(px), (int)Math.Round(y)), c, TF | TextFormatFlags.PreserveGraphicsClipping);
        return size.Width;
    }
}

internal sealed class PanelFonts : IDisposable
{
    public readonly Font Big, Body, BodyBold, Small, SmallBold, Tiny, Icon, IconSmall, IconLarge;

    public PanelFonts(float scale)
    {
        var iconFamily = HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        Big = new Font("Segoe UI", 40 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        Body = new Font("Segoe UI", 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        BodyBold = new Font("Segoe UI Semibold", 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Small = new Font("Segoe UI", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        SmallBold = new Font("Segoe UI Semibold", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Tiny = new Font("Segoe UI", 10.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        Icon = new Font(iconFamily, 13 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        IconSmall = new Font(iconFamily, 10 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        IconLarge = new Font(iconFamily, 20 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    internal static bool HasFont(string name)
    {
        using var fonts = new System.Drawing.Text.InstalledFontCollection();
        return fonts.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        foreach (var f in new[] { Big, Body, BodyBold, Small, SmallBold, Tiny, Icon, IconSmall, IconLarge }) f.Dispose();
    }
}
