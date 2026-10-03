using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>
/// Desktop widget with sections for electricity price, Home Assistant, Cloudflare, system resources, network and audio.
/// Drawn entirely by hand (GDI+). Drag to move, click a header to collapse, drag a header to move the section,
/// double-click for the compact view, right-click for the menu.
/// </summary>
internal sealed partial class DashboardForm : Form
{
    private static CultureInfo Fmt => L.Culture;

    private const float LogicalWidth = 344;
    private const float Pad = 16;
    // PreserveGraphicsTranslateTransform: the text must move along when the content scrolls
    private const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                                       | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping;

    private readonly ElectricityPriceService _el;
    private readonly SystemMonitor _sys;
    private readonly NetworkMonitor _net;
    private readonly AudioService _audio;
    private readonly HomeAssistantService _ha;
    private readonly CloudflareService _cf;
    private readonly ServiceMonitor _services;
    private readonly ProxmoxService _pve;
    private readonly Func<string?> _externalIp;
    private readonly Func<string?> _cloudflareStatus;
    private readonly Func<Task> _updateDns;
    private bool _dnsUpdating;
    private int _hiddenTicks;
    private readonly Action _saveSettings;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly WidgetTip _tip = new();
    private readonly List<Hit> _hits = new();

    private AppSettings _settings;
    private Palette _p;
    private FontSet _f;
    private Point _mouse = new(-1, -1);
    private string? _hoverKey;
    private bool _placed;

    // Click vs. drag
    private bool _pressed;
    private Point _downPoint;
    private Hit? _downHit;
    private DateTime _lastModeToggle = DateTime.MinValue;

    private sealed record Hit(RectangleF Rect, string? Tip, Action? Click, Action<int>? Wheel = null);

    public DashboardForm(ElectricityPriceService el, SystemMonitor sys, NetworkMonitor net, AudioService audio, HomeAssistantService ha,
                         CloudflareService cf, ServiceMonitor services, ProxmoxService pve, AppSettings settings, Action saveSettings,
                         Func<string?> externalIp, Func<string?> cloudflareStatus, Func<Task> updateDns, ContextMenuStrip menu,
                         Func<(DateTime Time, string? Error)>? networkState = null)
    {
        _cf = cf;
        _services = services;
        _pve = pve;
        _updateDns = updateDns;
        _el = el;
        _sys = sys;
        _net = net;
        _audio = audio;
        _ha = ha;
        _settings = settings;
        _saveSettings = saveSettings;
        _externalIp = externalIp;
        _networkState = networkState ?? (() => (DateTime.MinValue, null));
        _cloudflareStatus = cloudflareStatus;
        _p = Palette.For(settings.Theme);

        Text = "LabWidge";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = _p.Bg;
        ContextMenuStrip = menu;
        TopMost = settings.WidgetTopMost;
        Opacity = Math.Clamp(settings.WidgetOpacity, 40, 100) / 100.0;
        Icon = AppIconProvider.GetIcon();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _f = new FontSet(DpiScale);
        ClientSize = new Size((int)U(LogicalWidth), (int)U(400));

        _sys.Sample();
        _net.Sample();
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            if (Visible)
            {
                _sys.Sample();
                _net.Sample();
                Invalidate();
            }
            else if (++_hiddenTicks % 10 == 0)
            {
                _net.Sample();
            }
        };
        _timer.Start();

        _dragTimer.Tick += (_, _) => OnDragTick();
        _el.Updated += OnDataUpdated;
        _ha.Updated += OnDataUpdated;
        _cf.Updated += OnDataUpdated;
        _services.Updated += OnDataUpdated;
        _pve.Updated += OnDataUpdated;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private float DpiScale => DeviceDpi / 96f;
    private float U(float v) => v * DpiScale;

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _p = Palette.For(settings.Theme);
        BackColor = _p.Bg;
        TopMost = settings.WidgetTopMost;
        Opacity = Math.Clamp(settings.WidgetOpacity, 40, 100) / 100.0;
        ApplyBorderColor();
        if (Visible) FitSize();
        Invalidate();
    }

    public void ToggleVisible()
    {
        if (Visible) Hide();
        else ShowWidget();
    }

    public void ShowWidget()
    {
        FitSize();
        if (!_placed)
        {
            PlaceInitial();
            _placed = true;
        }
        EnsureOnScreen();
        Show();
        if (TopMost) BringToFront();
    }

    public void SetCompact(bool compact)
    {
        if (_settings.CompactMode == compact) return;
        _settings.CompactMode = compact;
        _lastModeToggle = DateTime.Now;
        _saveSettings();
        _tip.HideTip();
        FitSize();
        Invalidate();
    }

    private void OnDataUpdated()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => Invalidate()));
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme != WidgetTheme.System || !IsHandleCreated || IsDisposed) return;
        BeginInvoke(new Action(() => ApplySettings(_settings)));
    }

    // ---------- Window ----------

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: no Alt+Tab / taskbar
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _tip.HideTip();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch
        {
            // Older Windows: square corners.
        }
        ApplyBorderColor();
    }

    private void ApplyBorderColor()
    {
        if (!IsHandleCreated) return;
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

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _f.Dispose();
        _f = new FontSet(DpiScale);
        FitSize();
        Invalidate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _el.Updated -= OnDataUpdated;
            _ha.Updated -= OnDataUpdated;
            _cf.Updated -= OnDataUpdated;
            _services.Updated -= OnDataUpdated;
            _pve.Updated -= OnDataUpdated;
            _timer.Dispose();
            _dragTimer.Dispose();
            _drag?.Snapshot.Dispose();
            _tip.Dispose();
            _sectionMenu?.Dispose();
            _f.Dispose();
        }
        base.Dispose(disposing);
    }

    private void PlaceInitial()
    {
        // The bottom is the anchor: the widget stays put, however tall it is at start
        if (_settings.WidgetLeft is int l && (_settings.WidgetBottom ?? _settings.WidgetTop + Height) is int b
            && SystemInformation.VirtualScreen.Contains(new Point(l + 40, b - 20)))
        {
            Location = new Point(l, b - Height);
            return;
        }
        var wa = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        Location = new Point(wa.Right - Width - (int)U(12), wa.Bottom - Height - (int)U(12));
    }

    private void EnsureOnScreen()
    {
        var wa = Screen.FromPoint(new Point(Left + Width / 2, Top + 20)).WorkingArea;
        var x = Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        var y = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        Location = new Point(x, y);
    }

    private bool UserPlaced => _settings.WidgetLeft != null;

    private void FitSize()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        SetSize(Render(g));
    }

    private void SetSize(SizeF size)
    {
        var target = new Size((int)Math.Ceiling(size.Width), (int)Math.Ceiling(ViewHeight(size.Height)));
        if (ClientSize == target) return;
        var dw = target.Width - ClientSize.Width;
        var dh = target.Height - ClientSize.Height;
        _settingSize = true;
        try { ClientSize = target; } finally { _settingSize = false; }
        if (!_placed) return;

        // Anchored at the bottom: a section that collapses or expands moves the top – not the bottom.
        // Without a stored position the widget is also anchored on the right (by the clock).
        Top -= dh;
        if (!UserPlaced) Left -= dw;
        EnsureOnScreen();
        if (UserPlaced) RememberPosition(save: false);
    }

    /// <summary>Remembers the position with the bottom as the anchor.</summary>
    private void RememberPosition(bool save)
    {
        _settings.WidgetLeft = Left;
        _settings.WidgetTop = Top;
        _settings.WidgetBottom = Bottom;
        if (save) _saveSettings();
    }

    // ---------- Mouse ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = ToContent(e.Location);

        if (_drag != null)
        {
            // When the card is dragged towards the edge of a scrolled widget, the content follows
            if (PinOf(_drag.Key) == SectionPin.None)
            {
                if (e.Y < _layout.MiddleTop + U(30)) ScrollTo(_scroll - U(14));
                else if (e.Y > _layout.BottomTop - U(30)) ScrollTo(_scroll + U(14));
            }
            _mouse = ToContent(e.Location);
            MoveSectionDrag(_mouse.Y + (PinOf(_drag.Key) == SectionPin.None ? (int)Math.Round(_scroll) : 0));
            return;
        }

        if (_pressed && (Math.Abs(e.X - _downPoint.X) > SystemInformation.DragSize.Width
                         || Math.Abs(e.Y - _downPoint.Y) > SystemInformation.DragSize.Height))
        {
            _pressed = false;
            _downHit = null;
            // Dragging a header moves the section – dragging anywhere else moves the widget
            if (_downSection != null && Math.Abs(e.Y - _downPoint.Y) >= Math.Abs(e.X - _downPoint.X)) StartSectionDrag(_downSection, _mouse);
            else BeginDrag();
            return;
        }

        var hit = _hits.LastOrDefault(h => h.Rect.Contains(_mouse));
        Cursor = SectionHeaderAt(_mouse) != null ? Cursors.SizeNS : hit?.Click != null ? Cursors.Hand : Cursors.Default;
        var key = hit == null ? null : $"{hit.Rect}|{hit.Tip}";
        if (key != _hoverKey)
        {
            _hoverKey = key;
            if (hit?.Tip != null) ShowTip(hit.Tip);
            else _tip.HideTip();
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_drag != null) return;
        _mouse = new Point(-1, -1);
        _hoverKey = null;
        _tip.HideTip();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        _downPoint = e.Location;
        _downHit = _hits.LastOrDefault(h => h.Click != null && h.Rect.Contains(ToContent(e.Location)));
        _downSection = SectionHeaderAt(ToContent(e.Location));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag != null)
        {
            DropSectionDrag();
            return;
        }
        if (e.Button != MouseButtons.Left || !_pressed) return;
        _pressed = false;
        var hit = _downHit;
        _downHit = null;
        if (hit != null && hit.Rect.Contains(ToContent(e.Location)))
        {
            hit.Click!();
        }
    }

    /// <summary>
    /// The scroll wheel over an audio button or the microphone adjusts the volume – one step per notch.
    /// Anywhere else it scrolls the content when the widget is taller than the screen.
    /// </summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_drag != null) return;
        var hit = _hits.LastOrDefault(h => h.Wheel != null && h.Rect.Contains(ToContent(e.Location)));
        if (hit == null)
        {
            ScrollTo(_scroll - e.Delta * U(48) / SystemInformation.MouseWheelScrollDelta);
            _mouse = ToContent(e.Location);
            _hoverKey = null;
            _tip.HideTip();
            return;
        }
        _wheelRest += e.Delta;
        var notches = _wheelRest / SystemInformation.MouseWheelScrollDelta;
        _wheelRest -= notches * SystemInformation.MouseWheelScrollDelta;
        if (notches == 0) return;
        hit.Wheel!(notches);
        _hoverKey = null; // the tooltip shows the new volume on the next mouse move
        Invalidate();
    }

    private int _wheelRest;

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || _settings.CompactMode) return;
        if ((DateTime.Now - _lastModeToggle).TotalMilliseconds < 800) return;
        if (_hits.Any(h => h.Click != null && h.Rect.Contains(ToContent(e.Location)))) return;
        SetCompact(true);
    }

    private void BeginDrag()
    {
        _tip.HideTip();
        var before = Location;
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
        if (Location != before) RememberPosition(save: true);
    }

    /// <summary>Shows the text left of the widget, at the height of the mouse – so it never covers the content.</summary>
    private void ShowTip(string text, int durationMs = 0)
    {
        _tip.ShowBeside(this, text, Cursor.Position.Y, _p, Opacity, durationMs);
    }

    private void ShowTipAtMouse(string text) => ShowTip(text, 1500);

    private void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            ShowTipAtMouse(L.T("Copied: ", "Kopieret: ") + text);
            Logger.Info($"Widget: kopierede {text}.");
        }
        catch
        {
            ShowTipAtMouse(L.T("Could not copy", "Kunne ikke kopiere"));
        }
    }

    private void OpenDrive(string drive)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = drive.TrimEnd('\\') + "\\", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowTipAtMouse(L.T("Could not open the drive", "Kunne ikke åbne drevet"));
            Logger.Error($"Could not open {drive}: {ex.Message}");
        }
    }

    private void ToggleCollapsed(Func<bool> get, Action<bool> set)
    {
        var section = SectionAtHeader(_mouse);
        set(!get());
        FitSize();
        // A section expanded below the screen edge is scrolled into view – with its header visible
        if (!get() && section != null && PinOf(section) == SectionPin.None
            && _sectionBounds.FirstOrDefault(b => b.Key == section) is { } b && b.Bottom > _layout.BottomTop)
            ScrollTo(_scroll + Math.Min(b.Bottom - _layout.BottomTop, b.Top - _layout.MiddleTop));
        _saveSettings();
        Invalidate();
    }

    // ---------- Scroll ----------

    /// <summary>How far the content is scrolled. Only above zero when the widget is taller than the screen.</summary>
    private float _scroll;
    private float MaxScroll => _settings.CompactMode ? 0 : _layout.ScrollMax;

    private Point ToContent(Point p) => p; // Hit areas and headers are clipped and stored in screen coordinates.

    /// <summary>The widget may be at most as tall as the screen's working area – the rest scrolls.</summary>
    private float MaxViewHeight() => Screen.FromControl(this).WorkingArea.Height;

    private void ScrollTo(float value)
    {
        var clamped = Math.Clamp(value, 0, MaxScroll);
        if (Math.Abs(clamped - _scroll) < 0.5f) return;
        _scroll = clamped;
        Invalidate();
    }

    /// <summary>
    /// Instead of a scrollbar: the edge fades where there is more content, and a thin line shows
    /// the position while the mouse is over the widget.
    /// </summary>
    private void DrawScrollHints(Graphics g)
    {
        var max = MaxScroll;
        if (max <= 0) return;
        var w = ClientSize.Width;
        var h = _layout.MiddleHeight;
        var start = _layout.MiddleTop;
        if (h <= 0) return;
        var fade = U(22);
        if (_scroll > 0)
        {
            using var top = new LinearGradientBrush(new RectangleF(0, start, w, fade + 1), _p.Bg, Color.FromArgb(0, _p.Bg), LinearGradientMode.Vertical);
            g.FillRectangle(top, 0, start, w, Math.Min(fade, h));
        }
        if (_scroll < max)
        {
            using var bottom = new LinearGradientBrush(new RectangleF(0, start + h - fade - 1, w, fade + 1), Color.FromArgb(0, _p.Bg), _p.Bg, LinearGradientMode.Vertical);
            g.FillRectangle(bottom, 0, start + h - Math.Min(fade, h), w, Math.Min(fade, h));
        }
        if (_mouse.X < 0) return;
        var track = Math.Max(1, h - U(16));
        var thumb = Math.Min(track, Math.Max(U(28), track * h / (h + max)));
        var bar = new RectangleF(w - U(5), start + U(8) + (track - thumb) * _scroll / max, U(3), thumb);
        FillRound(g, bar, Color.FromArgb(_p.IsDark ? 90 : 70, _p.TextSecondary), bar.Width / 2);
    }

    // ---------- Drawing ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_p.Bg);
        var size = Render(g);

        var height = ViewHeight(size.Height);
        if (Math.Abs(ClientSize.Height - height) > 1 || Math.Abs(ClientSize.Width - size.Width) > 1)
        {
            SetSize(size);
            Invalidate();
        }
        else if (_scroll > MaxScroll)
        {
            _scroll = MaxScroll;
            Invalidate();
        }
    }

    private SizeF Render(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        _hits.Clear();

        return _settings.CompactMode ? RenderCompact(g) : RenderFull(g);
    }

    private SizeF RenderFull(Graphics g)
    {
        return RenderPinned(g);
    }

    /// <summary>One line: price · CPU · RAM · IP. Click to expand.</summary>
    private SizeF RenderCompact(Graphics g)
    {
        var height = U(38);
        float x = U(14);
        var ty = (height - Measure(g, "0", _f.BodyBold).Height) / 2;
        var first = true;

        void Separator()
        {
            if (!first)
            {
                x += U(8);
                x += DrawText(g, "·", _f.Body, _p.TextDim, x, ty);
                x += U(8);
            }
            first = false;
        }

        if (_settings.PriceEnabled && CurrentPrice() is var (price, level) && price != null)
        {
            Separator();
            var c = _p.Level(level);
            x += DrawText(g, "", _f.Icon, c, x, ty + U(1)) + U(4);
            x += DrawText(g, Ore(price.Value), _f.BodyBold, c, x, ty) + U(3);
            x += DrawText(g, _settings.PriceUnit.Symbol, _f.Small, _p.TextSecondary, x, ty + U(1));
        }
        if (_settings.ShowSystem)
        {
            Separator();
            x += DrawText(g, "CPU ", _f.Small, _p.TextSecondary, x, ty + U(1));
            x += DrawText(g, $"{_sys.CpuPercent:0} %", _f.BodyBold, _p.TextPrimary, x, ty);
            x += U(10);
            var ram = _sys.RamTotal == 0 ? 0 : _sys.RamUsed * 100.0 / _sys.RamTotal;
            x += DrawText(g, "RAM ", _f.Small, _p.TextSecondary, x, ty + U(1));
            x += DrawText(g, $"{ram:0} %", _f.BodyBold, _p.TextPrimary, x, ty);
            if (_sys.Gpu.Available)
            {
                x += U(10);
                x += DrawText(g, "GPU ", _f.Small, _p.TextSecondary, x, ty + U(1));
                x += DrawText(g, $"{_sys.Gpu.Percent:0} %", _f.BodyBold, _p.TextPrimary, x, ty);
                if (_sys.Gpu.TempC is int t)
                {
                    x += U(6);
                    x += DrawText(g, $"{t} °C", _f.Small, TempColor(t), x, ty + U(1));
                }
            }
        }
        if (_settings.ShowNetwork)
        {
            Separator();
            var ip = _externalIp() ?? _net.Adapters.FirstOrDefault()?.Ipv4 ?? "offline";
            x += DrawText(g, ip, _f.BodyBold, _p.TextPrimary, x, ty);
        }
        if (first)
        {
            x += DrawText(g, "LabWidge", _f.BodyBold, _p.TextPrimary, x, ty);
        }

        var width = x + U(14);
        _hits.Add(new Hit(new RectangleF(0, 0, width, height), L.T("Click to expand · drag to move", "Klik for at udvide · træk for at flytte"), () => SetCompact(false)));
        return new SizeF(width, height);
    }

    private (double? Price, PriceLevel Level) CurrentPrice()
    {
        var cur = _el.Current(DateTime.Now);
        if (cur == null) return (null, PriceLevel.Medium);
        var values = _el.HourlyConsumer(_settings).Select(h => h.Value).ToList();
        var total = _el.Consumer(cur.Time, cur.Spot, _settings);
        return (total, ElectricityPriceService.Level(total, values));
    }

    private float DrawPrice(Graphics g, float x, float y, float w)
    {
        var s = _settings;
        var now = DateTime.Now;
        var cur = _el.Current(now);
        var country = s.PriceCountry;
        var area = country?.AreaOrDefault(s.PriceArea);
        var title = L.T("ELECTRICITY", "ELPRIS") + "  ·  " + (country == null ? ""
            : country.Areas.Count > 1 ? $"{area!.Code} {area.ShortName}" : country.Name);

        if (cur == null)
        {
            var msg = _el.LastError != null ? "⚠ offline" : L.T("fetching…", "henter…");
            y = Header(g, "", title, msg, _el.LastError != null ? _p.Amber : null, x, y, w,
                       s.CollapsedPrice, () => ToggleCollapsed(() => s.CollapsedPrice, v => s.CollapsedPrice = v));
            if (s.CollapsedPrice) return y - U(6);
            DrawText(g, _el.LastError != null ? L.T("Could not fetch prices – trying again", "Kunne ikke hente priser – prøver igen") : L.T("Fetching prices…", "Henter priser…"), _f.Body, _p.TextSecondary, x, y);
            return y + U(18);
        }

        var hours = _el.HourlyConsumer(s);
        var hourValues = hours.Select(h => h.Value).ToList();
        double lo = hourValues.Min(), hi = hourValues.Max();
        var bd = _el.Breakdown(cur.Time, cur.Spot, s);
        var level = ElectricityPriceService.Level(bd.Total, hourValues);
        var levelColor = _p.Level(level);
        var levelText = level switch { PriceLevel.Cheap => L.T("Cheap", "Billig"), PriceLevel.Medium => L.T("Medium", "Middel"), _ => L.T("Expensive", "Dyr") };

        string status;
        Color? statusColor = null;
        if (s.CollapsedPrice) { status = $"{Ore(bd.Total)} {Unit} · {levelText}"; statusColor = levelColor; }
        else if (_el.LastError != null) { status = L.T("⚠ update failed", "⚠ opdatering fejlede"); statusColor = _p.Amber; }
        else status = L.T("updated ", "opdateret ") + _el.LastFetch.ToString("HH:mm");

        y = Header(g, "", title, status, statusColor, x, y, w,
                   s.CollapsedPrice, () => ToggleCollapsed(() => s.CollapsedPrice, v => s.CollapsedPrice = v));
        if (s.CollapsedPrice) return y - U(6);

        // Large price + level
        var priceW = DrawText(g, Ore(bd.Total), _f.Big, levelColor, x - U(2), y - U(6));
        var px = x + priceW + U(8);
        var ls = Measure(g, levelText, _f.SmallBold);
        var pill = new RectangleF(px, y + U(2), ls.Width + U(12), ls.Height + U(4));
        FillRound(g, pill, Color.FromArgb(_p.IsDark ? 48 : 36, levelColor), pill.Height / 2);
        DrawText(g, levelText, _f.SmallBold, levelColor, pill.X + U(6), pill.Y + U(2));
        DrawText(g, s.PriceUnit.PerKwh, _f.Small, _p.TextSecondary, px, y + U(23));
        var what = s.PriceShowTotal ? L.T("total price", "samlet pris") : L.T("spot price", "spotpris");
        DrawText(g, $"{what}, " + (s.PriceInclVat ? L.T("incl. VAT", "inkl. moms") : L.T("excl. VAT", "ekskl. moms")), _f.Tiny, _p.TextDim, px, y + U(38));

        var tipLines = $"Spot {Ore(bd.Spot)} {Unit}";
        var danish = country?.Code == "DK";
        if (s.PriceShowTotal && !danish && s.SupplierAddOnOre != 0)
        {
            // Outside Denmark there are no Datahub tariffs: spot plus the user's own add-on
            DrawText(g, Ore(bd.Spot), _f.Tiny, _p.TextSecondary, x + w, y + U(1), right: true);
            DrawText(g, "Spot", _f.Tiny, _p.TextDim, x + w - U(30), y + U(1), right: true);
            DrawText(g, Ore(bd.Supplier), _f.Tiny, _p.TextSecondary, x + w, y + U(17), right: true);
            DrawText(g, L.T("Add-on", "Tillæg"), _f.Tiny, _p.TextDim, x + w - U(30), y + U(17), right: true);
            tipLines = L.T($"Spot {Ore(bd.Spot)} + add-on {Ore(bd.Supplier)}", $"Spot {Ore(bd.Spot)} + tillæg {Ore(bd.Supplier)}")
                       + $"\n= {Ore(bd.Total)} {s.PriceUnit.PerKwh}";
        }
        else if (s.PriceShowTotal && danish)
        {
            var rows = new List<(string Label, double Value)>
            {
                ("Spot", bd.Spot), (L.T("Grid tariff", "Nettarif"), bd.NetTariff), (L.T("Taxes", "Afgifter"), bd.StateCharges)
            };
            if (s.SupplierAddOnOre != 0) rows.Add((L.T("Supplier", "Elselskab"), bd.Supplier));
            var lineH = rows.Count > 3 ? U(12.5f) : U(16);
            var ry = y + U(1);
            foreach (var (label, value) in rows)
            {
                DrawText(g, Ore(value), _f.Tiny, _p.TextSecondary, x + w, ry, right: true);
                DrawText(g, label, _f.Tiny, _p.TextDim, x + w - U(30), ry, right: true);
                ry += lineH;
            }
            tipLines = L.T($"Spot {Ore(bd.Spot)} + grid tariff {Ore(bd.NetTariff)} + taxes {Ore(bd.StateCharges)}",
                           $"Spot {Ore(bd.Spot)} + nettarif {Ore(bd.NetTariff)} + afgifter {Ore(bd.StateCharges)}")
                       + (s.SupplierAddOnOre != 0 ? L.T($" + supplier {Ore(bd.Supplier)}", $" + elselskab {Ore(bd.Supplier)}") : "")
                       + $"\n= {Ore(bd.Total)} {s.PriceUnit.PerKwh}"
                       + (s.HasNetTariff ? "" : L.T("\nNo grid company chosen – the grid tariff is not included", "\nNetselskab ikke valgt – nettarif er ikke med"));
        }
        _hits.Add(new Hit(new RectangleF(x, y - U(4), w, U(54)), tipLines, null));
        y += U(56);

        // This quarter-hour and the next
        var next = _el.Next(now);
        var length = next != null && next.Time > cur.Time ? next.Time - cur.Time : TimeSpan.FromMinutes(15);
        if (length > TimeSpan.FromHours(1)) length = TimeSpan.FromHours(1);
        var (period, periodDa) = length >= TimeSpan.FromHours(1) ? ("hour", "time") : ("quarter", "kvarter");
        var sub = L.T("", "kl. ") + $"{cur.Time:HH:mm}–{cur.Time.Add(length):HH:mm}";
        if (next != null)
        {
            var nv = _el.Consumer(next.Time, next.Spot, s);
            var arrow = nv > bd.Total + 0.5 ? "↑" : nv < bd.Total - 0.5 ? "↓" : "→";
            sub += L.T($"    ·    next {period} {arrow} {Ore(nv)} {Unit}", $"    ·    næste {periodDa} {arrow} {Ore(nv)} {Unit}");
        }
        DrawText(g, sub, _f.Small, _p.TextSecondary, x, y);
        y += U(24);

        // Chart of hourly prices
        var chartH = U(58);
        var n = hours.Count;
        var slot = w / n;
        var gap = slot > U(5) ? U(1.5f) : U(0.6f);
        double bottom = Math.Min(0, lo), top = Math.Max(hi, bottom + 1);
        float Y(double v) => y + chartH - (float)((v - bottom) / (top - bottom)) * chartH;
        var y0 = Y(0);
        var curHour = now.Date.AddHours(now.Hour);
        var axisY = y + chartH + U(5);

        for (var i = 0; i < n; i++)
        {
            var (hour, value) = hours[i];
            var yv = Y(value);
            var alpha = hour < curHour ? 70 : hour == curHour ? 255 : 205;
            var c = Color.FromArgb(alpha, _p.Level(ElectricityPriceService.Level(value, hourValues)));
            var height = Math.Max(U(2), Math.Abs(y0 - yv));
            var rect = new RectangleF(x + i * slot, value >= 0 ? y0 - height : y0, Math.Max(1f, slot - gap), height);
            FillRound(g, rect, c, Math.Min(U(1.5f), rect.Width / 2));

            var hitRect = new RectangleF(x + i * slot, y - U(4), slot, chartH + U(6));
            _hits.Add(new Hit(hitRect, hour.ToString(L.T("dddd d MMM", "dddd d. MMM"), Fmt) + L.T(" ", " kl. ") + $"{hour:HH}–{hour.AddHours(1):HH}\n{Ore(value)} {s.PriceUnit.PerKwh}", null));

            if (hour.Hour % 6 == 0)
            {
                var tomorrow = hour.Hour == 0 && i > 0;
                if (hour.Hour == 6 && hour.Date > now.Date) continue;
                DrawText(g, tomorrow ? L.T("tomorrow", "i morgen") : hour.ToString("HH"), _f.Tiny, _p.TextDim, x + i * slot, axisY);
                if (tomorrow)
                {
                    using var dash = new Pen(Color.FromArgb(70, _p.TextSecondary), Math.Max(1f, DpiScale)) { DashStyle = DashStyle.Dot };
                    g.DrawLine(dash, x + i * slot - gap / 2, y - U(2), x + i * slot - gap / 2, axisY + U(12));
                }
            }
        }

        var nowIndex = hours.ToList().FindIndex(h => h.Hour == curHour);
        if (nowIndex >= 0)
        {
            var nx = x + (nowIndex + now.Minute / 60f) * slot;
            using var pen = new Pen(Color.FromArgb(230, _p.TextPrimary), Math.Max(1f, U(1.2f)));
            g.DrawLine(pen, nx, y - U(4), nx, y + chartH + U(1));
        }
        y = axisY + U(20);

        // Key figures
        var today = hours.Where(h => h.Hour.Date == now.Date).Select(h => h.Value).ToList();
        if (today.Count > 0)
        {
            DrawText(g, L.T($"Today   min {Ore(today.Min())}   ·   avg {Ore(today.Average())}   ·   max {Ore(today.Max())}",
                            $"I dag   min {Ore(today.Min())}   ·   gns. {Ore(today.Average())}   ·   maks {Ore(today.Max())}"), _f.Small, _p.TextSecondary, x, y);
            y += U(19);
        }

        var future = hours.Where(h => h.Hour >= curHour).ToList();
        if (future.Count >= 3)
        {
            var best = 0;
            var bestAvg = double.MaxValue;
            for (var i = 0; i <= future.Count - 3; i++)
            {
                var avg = (future[i].Value + future[i + 1].Value + future[i + 2].Value) / 3;
                if (avg < bestAvg) { bestAvg = avg; best = i; }
            }
            var start = future[best].Hour;
            var day = start.Date == now.Date ? L.T("today", "i dag") : L.T("tomorrow", "i morgen");
            var lw = DrawText(g, L.T("Cheapest 3 hours  ", "Billigste 3 timer  "), _f.Small, _p.TextSecondary, x, y);
            DrawText(g, L.T($"{day} {start:HH}–{start.AddHours(3):HH}  ·  avg {Ore(bestAvg)} {Unit}", $"{day} kl. {start:HH}–{start.AddHours(3):HH}  ·  gns. {Ore(bestAvg)} {Unit}"), _f.SmallBold, _p.Green, x + lw, y);
            y += U(19);
        }

        if (!_el.HasTomorrow)
        {
            DrawText(g, danish ? L.T("Tomorrow's prices arrive around 13:00", "Morgendagens priser kommer ca. kl. 13")
                               : L.T("Tomorrow's prices arrive in the early afternoon", "Morgendagens priser kommer først på eftermiddagen"),
                _f.Small, _p.TextDim, x, y);
            y += U(19);
        }
        if (s.PriceShowTotal && _el.TariffError != null)
        {
            DrawText(g, L.T("⚠ Tariffs could not be fetched – grid tariff missing", "⚠ Tariffer kunne ikke hentes – nettarif mangler"), _f.Small, _p.Amber, x, y);
            y += U(19);
        }
        return y - U(3);
    }

    private float DrawSystem(Graphics g, float x, float y, float w)
    {
        var s = _settings;
        var ramFrac = _sys.RamTotal == 0 ? 0 : (double)_sys.RamUsed / _sys.RamTotal;
        var gpu = _sys.Gpu;
        var right = s.CollapsedSystem
            ? $"CPU {_sys.CpuPercent:0} %  ·  RAM {ramFrac * 100:0} %" + (gpu.Available ? $"  ·  GPU {gpu.Percent:0} %" : "")
            : L.T("up ", "tændt i ") + Uptime(_sys.Uptime);
        y = Header(g, "", "SYSTEM", right, s.CollapsedSystem ? _p.TextSecondary : null, x, y, w,
                   s.CollapsedSystem, () => ToggleCollapsed(() => s.CollapsedSystem, v => s.CollapsedSystem = v));
        if (s.CollapsedSystem) return y - U(6);

        var labelW = U(46);
        var valueW = U(112);

        // CPU with sparkline
        DrawText(g, "CPU", _f.Body, _p.TextSecondary, x, y + U(4));
        var cpuColor = _sys.CpuPercent > 90 ? _p.Red : _sys.CpuPercent > 70 ? _p.Amber : _p.TextPrimary;
        DrawText(g, $"{_sys.CpuPercent:0} %", _f.BodyBold, cpuColor, x + w, y + U(4), right: true);
        var spark = new RectangleF(x + labelW, y, w - labelW - U(56), U(24));
        DrawSparkline(g, spark, _sys.CpuHistory, 100, _p.Blue, fill: true);
        _hits.Add(new Hit(new RectangleF(x, y - U(2), w, U(28)), _sys.CpuName + L.T("\nCPU usage over the last minute", "\nCPU-forbrug det seneste minut"), null));
        y += U(32);

        y = BarRow(g, "RAM", ramFrac, $"{Gb(_sys.RamUsed)} / {Gb(_sys.RamTotal)} GB", x, y, w, labelW, valueW,
                   L.T($"Memory: {ramFrac * 100:0} % used\n{Gb(_sys.RamTotal - _sys.RamUsed)} GB free", $"Hukommelse: {ramFrac * 100:0} % brugt\n{Gb(_sys.RamTotal - _sys.RamUsed)} GB fri"), null);

        if (gpu.Available) y = DrawGpu(g, gpu, x, y, w, labelW, valueW);

        foreach (var d in _sys.Disks)
        {
            var frac = d.Total == 0 ? 0 : (double)d.Used / d.Total;
            var name = string.IsNullOrWhiteSpace(d.Label) ? d.Name : $"{d.Name} {d.Label}";
            var drive = d.Name;
            y = BarRow(g, d.Name, frac, $"{Gb(d.Used)} / {Gb(d.Total)} GB", x, y, w, labelW, valueW,
                       name + L.T($"\n{frac * 100:0} % used · {Gb(d.Total - d.Used)} GB free\nClick to open", $"\n{frac * 100:0} % brugt · {Gb(d.Total - d.Used)} GB fri\nKlik for at åbne"), () => OpenDrive(drive));
        }
        return y - U(4);
    }

    private float DrawGpu(Graphics g, GpuMonitor gpu, float x, float y, float w, float labelW, float valueW)
    {
        y += U(6);
        DrawText(g, "GPU", _f.Body, _p.TextSecondary, x, y + U(4));
        var gpuColor = gpu.Percent > 90 ? _p.Red : gpu.Percent > 70 ? _p.Amber : _p.TextPrimary;
        DrawText(g, $"{gpu.Percent:0} %", _f.BodyBold, gpuColor, x + w, y + U(4), right: true);
        var spark = new RectangleF(x + labelW, y, w - labelW - U(56), U(24));
        DrawSparkline(g, spark, gpu.History, 100, _p.Green, fill: true);

        var tip = new List<string> { gpu.Name, L.T("GPU load over the last minute", "GPU-belastning det seneste minut") };
        if (gpu.ClockMhz is int mhz) tip.Add(L.T($"Core: {mhz} MHz", $"Kerne: {mhz} MHz"));
        if (gpu.Source == "Windows") tip.Add(L.T("Temperature requires an NVIDIA card", "Temperatur kræver et NVIDIA-kort"));
        _hits.Add(new Hit(new RectangleF(x, y - U(2), w, U(28)), string.Join("\n", tip), null));
        y += U(32);

        if (gpu.VramTotal > 0)
        {
            var frac = (double)gpu.VramUsed / gpu.VramTotal;
            y = BarRow(g, "VRAM", frac, $"{Gb(gpu.VramUsed)} / {Gb(gpu.VramTotal)} GB", x, y, w, labelW, valueW,
                       L.T($"Graphics memory: {frac * 100:0} % used\n{Gb(gpu.VramTotal - gpu.VramUsed)} GB free", $"Grafikhukommelse: {frac * 100:0} % brugt\n{Gb(gpu.VramTotal - gpu.VramUsed)} GB fri"), null);
        }

        // Sensor line: temperature · power · fan (NVIDIA only)
        if (gpu.TempC != null || gpu.PowerW != null || gpu.FanPercent != null)
        {
            var sx = x + labelW;
            var sy = y + U(1);
            var first = true;
            void Part(string text, Color c)
            {
                if (!first) sx += DrawText(g, "  ·  ", _f.Small, _p.TextDim, sx, sy);
                sx += DrawText(g, text, _f.Small, c, sx, sy);
                first = false;
            }
            if (gpu.TempC is int t) Part($"{t} °C", TempColor(t));
            if (gpu.PowerW is double pw) Part(gpu.PowerLimitW is double pl ? $"{pw:0} / {pl:0} W" : $"{pw:0} W", _p.TextSecondary);
            if (gpu.FanPercent is int fan) Part(L.T($"fan {fan} %", $"blæser {fan} %"), _p.TextSecondary);
            _hits.Add(new Hit(new RectangleF(x, y - U(3), w, U(20)), L.T("Temperature · power draw / limit · fan speed", "Temperatur · strømforbrug / grænse · blæserhastighed"), null));
            y += U(21);
        }
        return y;
    }

    private Color TempColor(int celsius) => celsius >= 85 ? _p.Red : celsius >= 75 ? _p.Amber : _p.Green;

    private float DrawNetwork(Graphics g, float x, float y, float w)
    {
        var s = _settings;
        var primary = _net.Adapters.FirstOrDefault();
        var ext = _externalIp();

        string right;
        Color? rightColor = null;
        if (s.CollapsedNetwork)
        {
            right = $"{ext ?? primary?.Ipv4 ?? "offline"}  ·  {(_net.PingMs is long pm ? $"{pm} ms" : "timeout")}";
            rightColor = _p.TextSecondary;
        }
        else if (primary == null)
        {
            right = L.T("no connection", "ingen forbindelse");
            rightColor = _p.Red;
        }
        else
        {
            right = $"{primary.Kind}{(primary.SpeedBps > 0 ? " · " + LinkSpeed(primary.SpeedBps) : "")}";
        }
        y = Header(g, "", L.T("NETWORK", "NETVÆRK"), right, rightColor, x, y, w,
                   s.CollapsedNetwork, () => ToggleCollapsed(() => s.CollapsedNetwork, v => s.CollapsedNetwork = v));
        if (s.CollapsedNetwork) return y - U(6);

        var keyW = U(86);
        y = KeyValue(g, L.T("External IP", "Ekstern IP"), ext ?? L.T("fetching…", "henter…"), ext != null ? _p.TextPrimary : _p.TextDim, x, y, w, keyW, bold: true, copy: ext);

        if (primary != null)
        {
            y = KeyValue(g, L.T("Local IP", "Intern IP"), primary.Ipv4, _p.TextPrimary, x, y, w, keyW, bold: true, copy: primary.Ipv4,
                         suffix: $"/{primary.PrefixLength}  {Shorten(primary.Name, 18)}");
            foreach (var a in _net.Adapters.Skip(1).Take(2))
            {
                y = KeyValue(g, "", a.Ipv4, _p.TextSecondary, x, y, w, keyW, copy: a.Ipv4, suffix: $"  {Shorten(a.Name, 24)}");
            }
            if (primary.Gateway != null)
            {
                y = KeyValue(g, "Gateway", primary.Gateway, _p.TextPrimary, x, y, w, keyW, copy: primary.Gateway);
            }
            if (primary.Dns.Length > 0)
            {
                y = KeyValue(g, "DNS", string.Join(", ", primary.Dns.Take(2)), _p.TextPrimary, x, y, w, keyW, copy: string.Join(", ", primary.Dns));
            }
        }

        if (_net.PingMs is long ping)
        {
            var pc = ping < 50 ? _p.Green : ping < 150 ? _p.Amber : _p.Red;
            y = KeyValue(g, "Ping", $"{ping} ms", pc, x, y, w, keyW, bold: true, suffix: L.T("  to ", "  til ") + _net.PingTarget);
        }
        else
        {
            y = KeyValue(g, "Ping", "timeout", _p.Red, x, y, w, keyW, bold: true, suffix: L.T("  to ", "  til ") + _net.PingTarget);
        }

        if (!ShowsCloudflare && _cloudflareStatus() is string cf)
        {
            y = KeyValue(g, "Cloudflare", cf, cf.Contains(L.T("error", "fejl"), StringComparison.OrdinalIgnoreCase) ? _p.Amber : _p.TextSecondary, x, y, w, keyW);
        }

        y += U(6);
        DrawText(g, "↓ " + Bps(_net.DownBps), _f.BodyBold, _p.Green, x, y);
        DrawText(g, "↑ " + Bps(_net.UpBps), _f.BodyBold, _p.Blue, x + w, y, right: true);
        y += U(22);

        var chart = new RectangleF(x, y, w, U(30));
        var max = Math.Max(Math.Max(_net.DownHistory.Max(), _net.UpHistory.Max()), 200_000);
        using (var pen = new Pen(_p.Line, Math.Max(1f, DpiScale)))
        {
            g.DrawLine(pen, chart.Left, chart.Bottom, chart.Right, chart.Bottom);
        }
        DrawSparkline(g, chart, _net.DownHistory, max, _p.Green, fill: true);
        DrawSparkline(g, chart, _net.UpHistory, max, _p.Blue, fill: false);
        _hits.Add(new Hit(chart, L.T($"Traffic over the last minute\nPeak: {Bps(max)}", $"Trafik det seneste minut\nTop: {Bps(max)}"), null));
        return y + U(30);
    }

    /// <summary>The audio outputs the user chose to show – or all non-virtual ones if none are chosen.</summary>
    private List<AudioDevice> VisibleAudioDevices()
    {
        var all = _audio.Devices;
        if (_settings.AudioDeviceIds is { } ids)
            return ids.Select(id => all.FirstOrDefault(d => d.Id == id)).OfType<AudioDevice>().ToList();
        return all.Where(d => !AudioService.HiddenByDefault(d)).ToList();
    }

    private string AudioName(AudioDevice d) =>
        _settings.AudioNames.TryGetValue(d.Id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : d.Name;

    private static string AudioGlyph(AudioKind kind) => kind switch
    {
        AudioKind.Headphones => "",
        AudioKind.Headset => "",
        AudioKind.Display => "",
        AudioKind.Speakers or AudioKind.Digital => "",
        _ => ""
    };

    /// <summary>One button per audio output. A click makes it the Windows default device.</summary>
    private float DrawAudio(Graphics g, float x, float y, float w)
    {
        var s = _settings;
        var devices = VisibleAudioDevices();
        var active = _audio.Devices.FirstOrDefault(d => d.Id == _audio.DefaultId);
        var right = active == null ? L.T("none", "ingen") : devices.Contains(active) ? AudioName(active) : Shorten(active.FullName, 28);
        y = Header(g, "", L.T("AUDIO", "LYD"), right, s.CollapsedAudio ? _p.TextSecondary : null, x, y, w,
                   s.CollapsedAudio, () => ToggleCollapsed(() => s.CollapsedAudio, v => s.CollapsedAudio = v));
        if (s.CollapsedAudio) return y - U(6);

        var gap = U(8);
        var perRow = Math.Max(1, (int)((w + gap) / (U(64) + gap)));
        var cols = Math.Min(devices.Count, perRow);
        var tileW = (w - gap * (cols - 1)) / cols;
        var tileH = U(54);
        y += U(2);

        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var col = i % perRow;
            if (i > 0 && col == 0) y += tileH + gap;
            var tile = new RectangleF(x + col * (tileW + gap), y, tileW, tileH);
            var isActive = d.Id == _audio.DefaultId;
            var hover = tile.Contains(_mouse);

            var battery = _audio.Batteries.GetValueOrDefault(d.Id);
            if (isActive)
            {
                FillRound(g, tile, Color.FromArgb(_p.IsDark ? 55 : 35, _p.Blue), U(7));
                if (battery != null) DrawBatteryLevel(g, battery, tile);
            }
            else
            {
                FillRound(g, tile, hover ? _p.HoverBg : _p.Bg, U(7));
                if (battery != null) DrawBatteryLevel(g, battery, tile);
                using var path = WidgetIcon.RoundedRect(tile, U(7));
                using var pen = new Pen(_p.Line, Math.Max(1f, DpiScale));
                g.DrawPath(pen, path);
            }

            var volume = _audio.Volumes.GetValueOrDefault(d.Id);
            var showVolume = s.AudioShowVolume && volume != null;
            if (showVolume) DrawVolumeBar(g, volume!, tile, isActive ? _p.Blue : _p.TextSecondary);

            var color = isActive ? _p.Blue : hover ? _p.TextPrimary : _p.TextSecondary;
            var glyph = AudioGlyph(d.Kind);
            var gs = Measure(g, glyph, _f.IconLarge);
            DrawText(g, glyph, _f.IconLarge, color, tile.X + (tile.Width - gs.Width) / 2, tile.Y + U(7));

            var name = AudioName(d);
            var label = name;
            while (label.Length > 2 && Measure(g, label, _f.Tiny).Width > tile.Width - U(8)) label = label[..^2];
            if (label != name) label = label.TrimEnd() + "…";
            var ls = Measure(g, label, _f.Tiny);
            DrawText(g, label, _f.Tiny, isActive ? _p.Blue : _p.TextSecondary, tile.X + (tile.Width - ls.Width) / 2, tile.Bottom - U(showVolume ? 21 : 19));

            var isStandard = d.Id == s.AudioStandardId;
            if (isStandard) DrawText(g, "", _f.IconSmall, _p.Amber, tile.Right - U(15), tile.Y + U(5));

            if (battery != null) DrawBattery(g, battery, tile, tile.X + (tile.Width - gs.Width) / 2, isActive ? _p.Blue : _p.TextSecondary);

            var tip = d.FullName + "\n" + (isActive ? L.T("Active audio device", "Aktiv lydenhed") : L.T("Click to switch to it", "Klik for at skifte hertil"))
                    + (isStandard ? L.T("\nDefault device", "\nStandardenhed") : "")
                    + (battery == null ? "" : L.T($"\nBattery: {battery.Percent} %", $"\nBatteri: {battery.Percent} %") + (battery.Charging ? L.T(" – charging", " – lader") : ""))
                    + (showVolume ? L.T($"\nVolume: {VolumeText(volume)} · scroll to adjust", $"\nLydstyrke: {VolumeText(volume)} · scroll for at justere") : "");
            var id = d.Id;
            _hits.Add(new Hit(tile, tip, isActive ? null : () => SwitchAudio(id), showVolume ? n => ChangeVolume(id, n) : null));
        }
        y += tileH;
        if (s.AudioShowMic) y = DrawMicrophone(g, x, y + gap, w);
        return y;
    }

    /// <summary>The user's chosen battery colour, otherwise green while charging and <paramref name="normal"/> otherwise. A low level can override it.</summary>
    private Color BatteryColor(BatteryState battery, Color normal)
    {
        if (_settings.AudioBatteryWarn && !battery.Charging)
        {
            if (battery.Percent <= 15) return _p.Red;
            if (battery.Percent <= 30) return _p.Amber;
        }
        if (BatteryColorSetting.Parse(_settings.AudioBatteryColor) is { } custom) return custom;
        return battery.Charging ? _p.Green : normal;
    }

    /// <summary>The button fills from the bottom like a battery: the height follows the level, the colour changes at low levels.</summary>
    private void DrawBatteryLevel(Graphics g, BatteryState battery, RectangleF tile)
    {
        var height = tile.Height * Math.Clamp(battery.Percent, 0, 100) / 100f;
        if (height <= 0) return;
        using var path = WidgetIcon.RoundedRect(tile, U(7));
        using var clip = g.Clip;
        g.SetClip(path, System.Drawing.Drawing2D.CombineMode.Intersect);
        using var brush = new SolidBrush(Color.FromArgb(_p.IsDark ? 50 : 40, BatteryColor(battery, _p.Green)));
        g.FillRectangle(brush, tile.X, tile.Bottom - height, tile.Width, height);
        g.Clip = clip;
    }

    /// <summary>Battery percentage at the top left of the button, with a bolt while charging. Without the % sign if the button is narrow.</summary>
    private void DrawBattery(Graphics g, BatteryState battery, RectangleF tile, float glyphLeft, Color normal)
    {
        var color = BatteryColor(battery, normal);
        var x = tile.X + U(6);
        var y = tile.Y + U(4);
        var bolt = battery.Charging ? Measure(g, "", _f.IconSmall).Width : 0;
        var text = $"{battery.Percent}%";
        if (x + bolt + Measure(g, text, _f.Tiny).Width > glyphLeft - U(2)) text = battery.Percent.ToString();
        if (battery.Charging)
        {
            DrawText(g, "", _f.IconSmall, color, x - U(2), y + U(2));
            x += bolt - U(3);
        }
        DrawText(g, text, _f.Tiny, color, x, y);
    }

    /// <summary>Thin volume bar at the bottom of the button. Dimmed when the device is muted.</summary>
    private void DrawVolumeBar(Graphics g, VolumeState volume, RectangleF tile, Color color)
    {
        var track = new RectangleF(tile.X + U(8), tile.Bottom - U(5), tile.Width - U(16), Math.Max(2f, U(2)));
        FillRound(g, track, Color.FromArgb(_p.IsDark ? 40 : 30, _p.TextSecondary), track.Height / 2);
        var level = track with { Width = track.Width * volume.Level };
        if (level.Width >= 1) FillRound(g, level, volume.Muted ? Color.FromArgb(90, color) : color, track.Height / 2);
    }

    private void ChangeVolume(string id, int notches)
    {
        if (!_audio.Volumes.TryGetValue(id, out var current)) return;
        var step = Math.Clamp(_settings.AudioVolumeStep, 1, 20) / 100f;
        // Round to the step, so 47 % + 2 lands on 49 % and not 49.3 %
        var level = MathF.Round((current.Level + notches * step) * 100) / 100f;
        _audio.SetVolume(id, level);
        if (current.Muted && notches > 0) _audio.SetMute(id, false);
    }

    private static string VolumeText(VolumeState? v) =>
        v == null ? "" : v.Muted ? L.T("Muted", "Slået fra") : $"{(int)Math.Round(v.Level * 100)} %";

    /// <summary>The microphone below the audio buttons: a click mutes and unmutes it, scrolling changes the sensitivity.</summary>
    private float DrawMicrophone(Graphics g, float x, float y, float w)
    {
        var mic = _audio.Microphones.FirstOrDefault(m => m.Id == _audio.DefaultMicId);
        if (mic == null) return y;
        var volume = _audio.Volumes.GetValueOrDefault(mic.Id);
        var muted = volume?.Muted == true;
        var row = new RectangleF(x, y, w, U(28));
        var hover = row.Contains(_mouse);

        FillRound(g, row, muted ? Color.FromArgb(_p.IsDark ? 50 : 30, _p.Red) : hover ? _p.HoverBg : _p.Bg, U(7));
        using (var path = WidgetIcon.RoundedRect(row, U(7)))
        using (var pen = new Pen(muted ? Color.FromArgb(140, _p.Red) : _p.Line, Math.Max(1f, DpiScale)))
            g.DrawPath(pen, path);

        var color = muted ? _p.Red : hover ? _p.TextPrimary : _p.TextSecondary;
        var icon = new RectangleF(row.X + U(8), row.Y, U(16), row.Height);
        var gs = Measure(g, "", _f.IconSmall);
        DrawText(g, "", _f.IconSmall, color, icon.X + (icon.Width - gs.Width) / 2, row.Y + (row.Height - gs.Height) / 2);
        if (muted)
        {
            using var slash = new Pen(_p.Red, Math.Max(1.5f, U(1.6f)));
            g.DrawLine(slash, icon.X + U(2), row.Y + U(7), icon.Right - U(2), row.Bottom - U(7));
        }

        var right = VolumeText(volume);
        var rw = right.Length > 0 ? Measure(g, right, _f.Tiny).Width : 0;
        var barW = _settings.AudioShowVolume && volume != null && !muted ? Math.Min(U(70), w * 0.25f) : 0;
        var nameX = icon.Right + U(6);
        var nameMax = row.Right - U(10) - rw - (barW > 0 ? barW + U(8) : 0) - nameX - U(6);
        var name = AudioName(mic);
        var label = name;
        while (label.Length > 2 && Measure(g, label, _f.Small).Width > nameMax) label = label[..^2];
        if (label != name) label = label.TrimEnd() + "…";
        var ty = row.Y + (row.Height - Measure(g, "Ag", _f.Small).Height) / 2;
        DrawText(g, label, _f.Small, muted ? _p.Red : _p.TextPrimary, nameX, ty);
        DrawText(g, right, _f.Tiny, muted ? _p.Red : _p.TextSecondary, row.Right - U(10), ty + U(1), right: true);
        if (barW > 0)
        {
            var track = new RectangleF(row.Right - U(10) - rw - U(8) - barW, row.Y + row.Height / 2 - U(1.5f), barW, Math.Max(2f, U(3)));
            FillRound(g, track, Color.FromArgb(_p.IsDark ? 40 : 30, _p.TextSecondary), track.Height / 2);
            var level = track with { Width = track.Width * volume!.Level };
            if (level.Width >= 1) FillRound(g, level, _p.Blue, track.Height / 2);
        }

        var tip = mic.FullName + "\n" + (muted ? L.T("The microphone is muted – click to unmute", "Mikrofonen er slået fra – klik for at slå den til") : L.T("Click to mute the microphone", "Klik for at slå mikrofonen fra"))
                  + (_settings.AudioShowVolume && volume != null
                      ? L.T($"\nSensitivity: {(int)Math.Round(volume.Level * 100)} % · scroll to adjust", $"\nFølsomhed: {(int)Math.Round(volume.Level * 100)} % · scroll for at justere")
                      : "");
        var id = mic.Id;
        _hits.Add(new Hit(row, tip, () => ToggleMicMute(id),
                          _settings.AudioShowVolume ? n => ChangeVolume(id, n) : null));
        return row.Bottom;
    }

    private void ToggleMicMute(string id)
    {
        var muted = _audio.Volumes.GetValueOrDefault(id)?.Muted == true;
        if (!_audio.SetMute(id, !muted))
        {
            ToastHelper.Show("audio", muted ? L.T("The microphone could not be unmuted", "Mikrofonen kunne ikke slås til") : L.T("The microphone could not be muted", "Mikrofonen kunne ikke slås fra"),
                L.T("See app.log for details", "Se app.log for detaljer"), null,
                Array.Empty<(string, Action)>(), () => { }, expireAfter: TimeSpan.FromMinutes(2));
        }
        Invalidate();
    }

    private void SwitchAudio(string id)
    {
        if (!_audio.SwitchOutput(id, _settings))
        {
            ToastHelper.Show("audio", L.T("The audio device could not be switched", "Lydenheden kunne ikke skiftes"), L.T("See app.log for details", "Se app.log for detaljer"), null,
                Array.Empty<(string, Action)>(), () => { }, expireAfter: TimeSpan.FromMinutes(2));
        }
        Invalidate();
    }

    /// <summary>
    /// One line that opens the Home Assistant panel. The entities live in the panel instead of the widget,
    /// so the widget does not grow with the number of lights.
    /// </summary>
    private float DrawHomeAssistant(Graphics g, float x, float y, float w)
    {
        var entities = _ha.Entities;
        var lit = entities.Count(e => e.CanToggle && e.IsOn);

        string right;
        Color rightColor;
        if (_ha.LastError != null)
        {
            right = "⚠ " + Shorten(_ha.LastError, 20);
            rightColor = _p.Amber;
        }
        else if (entities.Count == 0)
        {
            right = L.T("fetching…", "henter…");
            rightColor = _p.TextDim;
        }
        else
        {
            right = lit == 0 ? L.T("all off", "alt slukket") : L.T($"{lit} on", $"{lit} tændt");
            rightColor = lit > 0 ? _p.Amber : _p.TextSecondary;
        }

        var tip = _settings.HomeAssistantDashboardPath is { Length: > 0 }
            ? L.T("Click to open your Home Assistant dashboard", "Klik for at åbne dit Home Assistant-dashboard")
            : L.T($"Click to open the panel with your {entities.Count} entities", $"Klik for at åbne panelet med dine {entities.Count} enheder");
        var next = Header(g, "", "HOME ASSISTANT", right, rightColor, x, y, w, true,
            () => HomeAssistantPanel.Toggle(_ha, _settings, _saveSettings, Bounds));
        _hits[^1] = _hits[^1] with { Tip = tip + "\n" + _hits[^1].Tip };
        return next - U(6);
    }

    private bool ShowsCloudflare => _settings.ShowCloudflare && _settings.HasCloudflare;

    /// <summary>Tunnels with status, whether the A records point to the current IP, and a shortcut to the details.</summary>
    private float DrawCloudflare(Graphics g, float x, float y, float w)
    {
        var s = _settings;
        var tunnels = _cf.Tunnels;
        var ip = _externalIp();
        var records = _cf.ManagedRecords(s);
        var stale = ip == null ? 0 : records.Count(r => r.Content != ip);
        var hosts = s.ServiceChecksEnabled ? ServiceMonitor.CheckableHosts(tunnels, s) : Array.Empty<string>();
        var down = _services.Down(hosts);

        string right;
        Color rightColor;
        if (tunnels.Count > 0 && tunnels.All(t => t.IsHealthy) && down.Count > 0)
        {
            right = down.Count == 1 ? L.T("⚠ 1 service not responding", "⚠ 1 tjeneste svarer ikke") : L.T($"⚠ {down.Count} services not responding", $"⚠ {down.Count} tjenester svarer ikke");
            rightColor = _p.Red;
        }
        else if (tunnels.Count > 0)
        {
            var up = tunnels.Count(t => t.IsHealthy);
            right = up == tunnels.Count ? L.T($"{up}/{tunnels.Count} tunnels up", $"{up}/{tunnels.Count} tunnels oppe") : L.T($"{tunnels.Count - up}/{tunnels.Count} tunnels failing", $"{tunnels.Count - up}/{tunnels.Count} tunnels med fejl");
            rightColor = tunnels.Any(t => t.IsDown) ? _p.Red : up < tunnels.Count ? _p.Amber : _p.Green;
        }
        else if (_cf.DnsError != null)
        {
            right = "⚠ " + Shorten(_cf.DnsError, 24);
            rightColor = _p.Amber;
        }
        else if (_cf.LastFetch == DateTime.MinValue)
        {
            right = L.T("fetching…", "henter…");
            rightColor = _p.TextDim;
        }
        else
        {
            right = stale > 0 ? L.T($"⚠ {stale} A records outdated", $"⚠ {stale} A-poster forældet") : L.T($"{records.Count} A record{(records.Count == 1 ? "" : "s")} up to date", $"{records.Count} A-poster ajour");
            rightColor = stale > 0 ? _p.Amber : _p.TextSecondary;
        }
        y = Header(g, "", "CLOUDFLARE", right, rightColor, x, y, w,
                   s.CollapsedCloudflare, () => ToggleCollapsed(() => s.CollapsedCloudflare, v => s.CollapsedCloudflare = v));
        if (s.CollapsedCloudflare) return y - U(6);

        Action openPanel = () => CloudflarePanel.Toggle(_cf, _services, s, _saveSettings, _externalIp, Bounds);

        foreach (var t in tunnels)
        {
            var row = new RectangleF(x - U(6), y - U(3), w + U(12), U(22));
            var hovered = row.Contains(_mouse);
            if (hovered) FillRound(g, row, _p.HoverBg, U(5));

            var dotColor = t.IsHealthy ? _p.Green : t.IsDown ? _p.Red : _p.Amber;
            using (var brush = new SolidBrush(dotColor)) g.FillEllipse(brush, new RectangleF(x + U(2), y + U(5), U(8), U(8)));

            var detail = t.IsHealthy
                ? L.T($"{t.Connections} conn.", $"{t.Connections} forb.") + (t.Colos.Length > 0 ? " · " + string.Join(", ", t.Colos.Take(2)) : "")
                : t.StatusText;
            var dw = DrawText(g, detail, _f.Small, t.IsHealthy ? _p.TextSecondary : dotColor, x + w, y + U(1), right: true);
            DrawText(g, Fit(g, t.Name, _f.Body, w - U(28) - dw), _f.Body, hovered ? _p.Blue : _p.TextPrimary, x + U(18), y);

            var tip = new List<string> { $"{t.Name}: {t.StatusText}" };
            if (t.Connections > 0) tip.Add(L.T($"{t.Connections} connections via {string.Join(", ", t.Colos)}", $"{t.Connections} forbindelser via {string.Join(", ", t.Colos)}"));
            if (t.OriginIp != null) tip.Add(L.T("From ", "Fra ") + t.OriginIp + (t.ClientVersion != null ? $" · cloudflared {t.ClientVersion}" : ""));
            tip.AddRange(t.Routes.Take(6).Select(r => "→ " + r.Hostname));
            if (t.Routes.Count > 6) tip.Add(L.T($"… and {t.Routes.Count - 6} more", $"… og {t.Routes.Count - 6} mere"));
            tip.Add(L.T("Click for details", "Klik for detaljer"));
            _hits.Add(new Hit(row, string.Join("\n", tip), openPanel));
            y += U(23);
        }

        if (_cf.TunnelError != null && _cf.LastFetch != DateTime.MinValue)
        {
            var text = _cf.TunnelError switch
            {
                CloudflareService.MissingAccountId => L.T("Tunnels: fill in the Account ID in Settings", "Tunnels: udfyld Account ID i Indstillinger"),
                var e when e.Contains("Tunnel: Read") => L.T("Tunnels: the token lacks the Tunnel permission", "Tunnels: tokenet mangler Tunnel-rettighed"),
                var e => "Tunnels: " + e
            };
            DrawText(g, Fit(g, text, _f.Small, w), _f.Small, tunnels.Count > 0 ? _p.Amber : _p.TextDim, x, y + U(1));
            _hits.Add(new Hit(new RectangleF(x, y - U(2), w, U(20)),
                text + L.T("\nRequires the Account ID and the permission Account → Cloudflare Tunnel → Read (Settings → Cloudflare)",
                           "\nKræver Account ID og rettigheden Account → Cloudflare Tunnel → Read (Indstillinger → Cloudflare)"), null));
            y += U(21);
        }
        else if (tunnels.Count > 0)
        {
            y += U(4);
        }

        var keyW = U(46);

        // Services: do the addresses behind the tunnels respond?
        if (hosts.Count > 0)
        {
            var checkedCount = hosts.Count(h => _services.Get(h) != null);
            DrawText(g, "Web", _f.Body, _p.TextSecondary, x, y);
            string text;
            Color color;
            var suspect = _services.Suspect(hosts);
            if (checkedCount == 0) { text = L.T("checking…", "tjekker…"); color = _p.TextDim; }
            else if (down.Count == 0 && suspect > 0) { text = L.T($"rechecking {suspect}…", $"tjekker {suspect} igen…"); color = _p.Amber; }
            else if (down.Count == 0) { text = L.T($"✓ {checkedCount}/{hosts.Count} respond", $"✓ {checkedCount}/{hosts.Count} svarer"); color = _p.Green; }
            else { text = "⚠ " + string.Join(", ", down.Select(d => d.Host.Split('.')[0])) + L.T(" not responding", " svarer ikke"); color = _p.Red; }
            DrawText(g, Fit(g, text, _f.Body, w - keyW), _f.Body, color, x + keyW, y);

            var tip = down.Count == 0
                ? L.T($"All {hosts.Count} addresses behind the tunnels respond. Checked every 5 minutes.", $"Alle {hosts.Count} adresser bag tunnellerne svarer. Tjekkes hvert 5. minut.")
                : string.Join("\n", down.Select(d => $"{d.Host}: {d.Describe()}"));
            if (s.IgnoredServices.Length > 0) tip += L.T($"\n{s.IgnoredServices.Length} addresses are not checked (right-click in the panel)", $"\n{s.IgnoredServices.Length} adresser tjekkes ikke (højreklik i panelet)");
            _hits.Add(new Hit(new RectangleF(x, y - U(2), w, U(20)), tip + L.T("\nClick for details", "\nKlik for detaljer"), openPanel));
            y += U(21);
        }

        // DNS: do the A records point to the IP we have now?
        DrawText(g, "DNS", _f.Body, _p.TextSecondary, x, y);
        string dns;
        Color dnsColor;
        if (_cf.DnsError != null) { dns = "⚠ " + _cf.DnsError; dnsColor = _p.Amber; }
        else if (_cf.LastFetch == DateTime.MinValue || ip == null) { dns = L.T("fetching…", "henter…"); dnsColor = _p.TextDim; }
        else if (records.Count == 0) { dns = L.T("no A records", "ingen A-poster"); dnsColor = _p.TextSecondary; }
        else if (stale == 0) { dns = L.T($"✓ {records.Count} A record{(records.Count == 1 ? "" : "s")} = {ip}", $"✓ {records.Count} A-poster = {ip}"); dnsColor = _p.Green; }
        else { dns = L.T($"⚠ {stale} of {records.Count} point to an old IP", $"⚠ {stale} af {records.Count} peger på gammel IP"); dnsColor = _p.Amber; }

        var button = _dnsUpdating ? L.T("updating…", "opdaterer…") : L.T("Update", "Opdatér");
        var bs = Measure(g, button, _f.Small);
        var btn = new RectangleF(x + w - bs.Width - U(20), y - U(2), bs.Width + U(24), U(20));
        var btnHover = !_dnsUpdating && btn.Contains(_mouse);
        if (btnHover) FillRound(g, btn, _p.HoverBg, U(4));
        DrawText(g, "", _f.IconSmall, btnHover ? _p.Blue : _p.TextDim, btn.X + U(4), y + U(3));
        DrawText(g, button, _f.Small, btnHover ? _p.Blue : _p.TextSecondary, btn.X + U(18), y + U(1));
        DrawText(g, Fit(g, dns, _f.Body, btn.X - x - keyW - U(6)), _f.Body, dnsColor, x + keyW, y);

        var staleList = ip == null ? "" : string.Join("\n", records.Where(r => r.Content != ip).Take(6).Select(r => $"{r.Name} → {r.Content}"));
        var dnsTip = dns + (staleList.Length > 0 ? "\n" + staleList : "")
                     + (_cloudflareStatus() is string status ? L.T("\nAutomatic update: ", "\nAutomatisk opdatering: ") + status : "")
                     + L.T("\nClick for all A records", "\nKlik for alle A-poster");
        _hits.Add(new Hit(new RectangleF(x, y - U(2), btn.X - x - U(4), U(20)), dnsTip, openPanel));
        _hits.Add(new Hit(btn, L.T("Point the A records at your current IP now", "Sæt A-posterne til din nuværende IP nu"), _dnsUpdating ? null : () => _ = UpdateDnsAsync()));
        y += U(21);

        if (_cloudflareStatus() is string cfStatus && cfStatus.StartsWith(L.T("error", "fejl"), StringComparison.OrdinalIgnoreCase))
        {
            DrawText(g, Fit(g, L.T("⚠ Last update ", "⚠ Sidste opdatering ") + cfStatus, _f.Small, w - keyW), _f.Small, _p.Amber, x + keyW, y);
            _hits.Add(new Hit(new RectangleF(x, y - U(2), w, U(20)), cfStatus, null));
            y += U(19);
        }

        // Shortcut to the panel with addresses and links
        var link = new RectangleF(x - U(6), y - U(1), w + U(12), U(22));
        var linkHover = link.Contains(_mouse);
        if (linkHover) FillRound(g, link, _p.HoverBg, U(5));
        var routes = tunnels.Sum(t => t.Routes.Count);
        var linkText = routes > 0 ? L.T($"Addresses and details ({routes})", $"Adresser og detaljer ({routes})") : L.T("Details", "Detaljer");
        DrawText(g, "", _f.IconSmall, _p.TextDim, x + w - U(10), y + U(5));
        DrawText(g, linkText, _f.Small, linkHover ? _p.Blue : _p.TextSecondary, x + w - U(18), y + U(2), right: true);
        _hits.Add(new Hit(link, L.T("Which addresses the tunnels forward, all A records and links to Cloudflare", "Hvilke adresser tunnellerne sender videre, alle A-poster og links til Cloudflare"), openPanel));
        return y + U(20);
    }

    /// <summary>The Proxmox host: CPU, RAM and storage, and how many machines are running. Click for the list of machines.</summary>
    private float DrawProxmox(Graphics g, float x, float y, float w)
    {
        var s = _settings;
        var guests = _pve.Guests;
        var running = guests.Count(gu => gu.IsRunning);
        var offline = _pve.Nodes.Where(n => !n.Online).ToList();

        string right;
        Color rightColor;
        if (_pve.LastError != null && _pve.Nodes.Count == 0) { right = "⚠ " + Shorten(_pve.LastError, 26); rightColor = _p.Amber; }
        else if (_pve.LastFetch == DateTime.MinValue) { right = L.T("fetching…", "henter…"); rightColor = _p.TextDim; }
        else if (offline.Count > 0) { right = $"⚠ {offline[0].Name} offline"; rightColor = _p.Red; }
        else { right = L.T($"{running}/{guests.Count} running", $"{running}/{guests.Count} kører"); rightColor = s.CollapsedProxmox ? _p.TextSecondary : _p.TextDim; }

        y = Header(g, "", "PROXMOX", right, rightColor, x, y, w,
                   s.CollapsedProxmox, () => ToggleCollapsed(() => s.CollapsedProxmox, v => s.CollapsedProxmox = v));
        if (s.CollapsedProxmox) return y - U(6);

        Action openPanel = () => ProxmoxPanel.Toggle(_pve, s, Bounds);
        var labelW = U(46);
        var valueW = U(112);

        if (_pve.Nodes.Count == 0)
        {
            var text = _pve.LastError ?? L.T("Fetching…", "Henter…");
            DrawText(g, Fit(g, text, _f.Small, w), _f.Small, _pve.LastError != null ? _p.Amber : _p.TextDim, x, y);
            _hits.Add(new Hit(new RectangleF(x, y - U(2), w, U(20)), text + L.T("\nSee Settings → Proxmox", "\nSe Indstillinger → Proxmox"), null));
            y += U(21);
        }

        foreach (var node in _pve.Nodes)
        {
            if (_pve.Nodes.Count > 1 || !node.Online)
            {
                DrawText(g, node.Name, _f.SmallBold, node.Online ? _p.TextSecondary : _p.Red, x, y);
                DrawText(g, node.Online ? L.T("up ", "oppe ") + ProxmoxPanel.Uptime(node.Uptime) : "offline", _f.Small, _p.TextDim, x + w, y, right: true);
                y += U(19);
            }
            if (!node.Online) continue;

            var nodeTip = $"{node.Name} · " + L.T("up ", "oppe ") + ProxmoxPanel.Uptime(node.Uptime);
            y = BarRow(g, "CPU", node.Cpu, L.T($"{node.Cpu * 100:0} % of {node.MaxCpu}", $"{node.Cpu * 100:0} % af {node.MaxCpu}"), x, y, w, labelW, valueW, nodeTip, openPanel);
            var mem = node.MaxMem == 0 ? 0 : (double)node.Mem / node.MaxMem;
            y = BarRow(g, "RAM", mem, $"{Gb(node.Mem)} / {Gb(node.MaxMem)} GB", x, y, w, labelW, valueW, nodeTip, openPanel);
        }

        // The largest storage – the others are in the panel
        if (_pve.Storages.FirstOrDefault() is { } storage)
        {
            var frac = storage.Total == 0 ? 0 : (double)storage.Used / storage.Total;
            y = BarRow(g, L.T("Storage", "Lager"), frac, $"{Gb(storage.Used)} / {Gb(storage.Total)} GB", x, y, w, labelW, valueW,
                       $"{storage.Name}: {frac * 100:0} % brugt" + (_pve.Storages.Count > 1 ? $"\n{_pve.Storages.Count - 1} lagre mere i panelet" : ""), openPanel);
        }

        // Machines: running and stopped, click for the list
        if (guests.Count > 0)
        {
            var link = new RectangleF(x - U(6), y - U(1), w + U(12), U(22));
            var hovered = link.Contains(_mouse);
            if (hovered) FillRound(g, link, _p.HoverBg, U(5));
            var stopped = guests.Count - running;
            DrawText(g, "VM/CT", _f.Body, hovered ? _p.Blue : _p.TextSecondary, x, y + U(1));
            var tx = x + labelW;
            tx += DrawText(g, L.T($"{running} running", $"{running} kører"), _f.Body, _p.TextPrimary, tx, y + U(1));
            if (stopped > 0) DrawText(g, $"  ·  {stopped} stoppet", _f.Body, _p.TextDim, tx, y + U(1));
            DrawText(g, "", _f.IconSmall, _p.TextDim, x + w - U(10), y + U(5));

            var busiest = guests.Where(gu => gu.IsRunning).OrderByDescending(gu => gu.Cpu).Take(3)
                .Select(gu => $"{gu.Name}: CPU {gu.Cpu * 100:0} %, RAM {Gb(gu.Mem)} GB");
            _hits.Add(new Hit(link, L.T("Busiest right now:\n", "Travleste lige nu:\n") + string.Join("\n", busiest)
                + L.T("\nClick for all machines – start, shut down and reboot", "\nKlik for alle maskiner – start, luk ned og genstart"), openPanel));
            y += U(22);
        }
        return y - U(2);
    }

    private async Task UpdateDnsAsync()
    {
        _dnsUpdating = true;
        Invalidate();
        try
        {
            await _updateDns();
        }
        finally
        {
            _dnsUpdating = false;
            Invalidate();
        }
    }

    /// <summary>Shortens a text with "…" so it fits the given width.</summary>
    private static string Fit(Graphics g, string text, Font font, float width)
    {
        if (Measure(g, text, font).Width <= width) return text;
        var shown = text;
        while (shown.Length > 3 && Measure(g, shown + "…", font).Width > width) shown = shown[..^1];
        return shown.TrimEnd() + "…";
    }

    // ---------- Building blocks ----------

    private float Header(Graphics g, string glyph, string title, string? right, Color? rightColor, float x, float y, float w,
                         bool collapsed, Action toggle)
    {
        var rect = new RectangleF(x - U(6), y - U(4), w + U(12), U(24));
        if (rect.Contains(_mouse)) FillRound(g, rect, _p.HoverBg, U(6));
        var key = _drawingSection;
        var freshness = StatusFor(key);
        if (freshness is { } status && (!collapsed || status.Health != DataHealth.Current))
        {
            right = status.Text;
            rightColor = FreshnessColor(status);
        }
        _hits.Add(new Hit(rect, (collapsed ? L.T("Click the arrow to expand", "Klik på pilen for at folde ud") : L.T("Click the arrow to collapse", "Klik på pilen for at folde sammen"))
            + L.T(" · drag the handle · right-click for more options", " · træk i grebet · højreklik for flere valg")
            + (key != null && PinOf(key) != SectionPin.None
                ? (PinOf(key) == SectionPin.Top ? L.T("\nPinned to the top", "\nFastgjort øverst") : L.T("\nPinned to the bottom", "\nFastgjort nederst"))
                : "")
            + (freshness is { } detail ? "\n" + detail.Detail : ""), key == null ? toggle : () => ToggleHeader(key, toggle)));
        DrawHeaderGrip(g, x, y, key);
        DrawText(g, glyph, _f.Icon, _p.TextSecondary, x + U(29), y + U(1));
        var titleWidth = Math.Min(Measure(g, title, _f.SmallBold).Width, w * 0.53f - U(50));
        DrawText(g, Fit(g, title, _f.SmallBold, Math.Max(U(28), titleWidth)), _f.SmallBold, _p.TextSecondary, x + U(50), y);
        DrawText(g, collapsed ? "" : "", _f.IconSmall, _p.TextDim, x + w - U(10), y + U(3));
        if (right != null)
        {
            DrawText(g, Fit(g, right, _f.Small, Math.Max(U(25), w - U(74) - titleWidth)), _f.Small,
                rightColor ?? _p.TextDim, x + w - U(18), y, right: true);
        }
        return y + U(24);
    }

    private float BarRow(Graphics g, string label, double frac, string value, float x, float y, float w, float labelW, float valueW,
                         string? tip, Action? click)
    {
        var row = new RectangleF(x - U(6), y - U(3), w + U(12), U(22));
        if (click != null && row.Contains(_mouse)) FillRound(g, row, _p.HoverBg, U(5));

        DrawText(g, label, _f.Body, click != null && row.Contains(_mouse) ? _p.Blue : _p.TextSecondary, x, y);
        DrawText(g, value, _f.Body, _p.TextPrimary, x + w, y, right: true);
        var bar = new RectangleF(x + labelW, y + U(6), w - labelW - valueW, U(6));
        FillRound(g, bar, _p.Track, bar.Height / 2);
        var color = frac > 0.9 ? _p.Red : frac > 0.8 ? _p.Amber : _p.Blue;
        var filled = new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * (float)Math.Clamp(frac, 0, 1)), bar.Height);
        FillRound(g, filled, color, bar.Height / 2);
        if (tip != null || click != null) _hits.Add(new Hit(row, tip, click));
        return y + U(23);
    }

    private float KeyValue(Graphics g, string key, string value, Color color, float x, float y, float w, float keyW,
                           bool bold = false, string? copy = null, string? suffix = null)
    {
        var font = bold ? _f.BodyBold : _f.Body;
        var room = w - keyW - (copy != null ? U(24) : 0);
        var fullValue = value;
        var size = Measure(g, value, font);
        if (size.Width > room)
        {
            while (value.Length > 4 && Measure(g, value + "…", font).Width > room)
                value = value[..^1];
            value = value.TrimEnd() + "…";
            size = Measure(g, value, font);
            if (copy == null) _hits.Add(new Hit(new RectangleF(x, y - U(2), w, U(20)), fullValue, null));
        }

        if (copy != null)
        {
            var rect = new RectangleF(x + keyW - U(5), y - U(2), size.Width + U(28), size.Height + U(4));
            var hovered = rect.Contains(_mouse);
            if (hovered) FillRound(g, rect, _p.HoverBg, U(4));
            _hits.Add(new Hit(rect, L.T("Click to copy", "Klik for at kopiere"), () => CopyText(copy)));
            if (hovered)
            {
                DrawText(g, "", _f.IconSmall, _p.TextSecondary, rect.Right - U(17), y + U(2));
                suffix = null; // make room for the copy icon
            }
        }

        if (key.Length > 0) DrawText(g, key, _f.Body, _p.TextSecondary, x, y);
        DrawText(g, value, font, color, x + keyW, y);
        if (suffix != null)
        {
            var sx = x + keyW + size.Width + U(2);
            var suffixRoom = x + w - sx;
            var text = suffix;
            while (text.Length > 3 && Measure(g, text, _f.Small).Width > suffixRoom)
                text = text[..^2];
            if (text != suffix) text = text.TrimEnd() + "…";
            DrawText(g, text, _f.Small, _p.TextDim, sx, y + U(1.5f));
        }
        return y + U(21);
    }

    private void DrawSparkline(Graphics g, RectangleF r, History h, double max, Color c, bool fill)
    {
        if (h.Count < 2 || max <= 0) return;
        var step = r.Width / (h.Capacity - 1);
        var offset = (h.Capacity - h.Count) * step;
        var pts = new PointF[h.Count];
        for (var i = 0; i < h.Count; i++)
        {
            var v = Math.Min(h[i], max) / max;
            pts[i] = new PointF(r.X + offset + i * step, r.Bottom - (float)v * r.Height);
        }

        if (fill)
        {
            using var path = new GraphicsPath();
            path.AddLines(pts);
            path.AddLine(pts[^1], new PointF(pts[^1].X, r.Bottom));
            path.AddLine(new PointF(pts[^1].X, r.Bottom), new PointF(pts[0].X, r.Bottom));
            path.CloseFigure();
            using var brush = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2),
                                                      Color.FromArgb(_p.IsDark ? 95 : 70, c), Color.FromArgb(8, c), 90f);
            g.FillPath(brush, path);
        }
        using var pen = new Pen(c, Math.Max(1f, U(1.4f))) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts);
    }

    private static void FillRound(Graphics g, RectangleF r, Color c, float radius)
    {
        using var path = WidgetIcon.RoundedRect(r, radius);
        using var brush = new SolidBrush(c);
        g.FillPath(brush, path);
    }

    private static SizeF Measure(Graphics g, string s, Font f) => TextRenderer.MeasureText(g, s, f, Size.Empty, TF);

    private static float DrawText(Graphics g, string s, Font f, Color c, float x, float y, bool right = false)
    {
        var size = Measure(g, s, f);
        var px = right ? x - size.Width : x;
        TextRenderer.DrawText(g, s, f, new Point((int)Math.Round(px), (int)Math.Round(y)), c, TF);
        return size.Width;
    }

    // ---------- Formatting ----------

    private string Ore(double v) => _settings.PriceUnit.Format(v, Fmt);
    private string Unit => _settings.PriceUnit.Symbol;

    private static string Gb(double bytes)
    {
        var gb = bytes / 1073741824.0;
        return gb >= 100 ? gb.ToString("#,0", Fmt) : gb.ToString("0.0", Fmt);
    }

    private static string Bps(double bps) =>
        bps >= 1e9 ? $"{(bps / 1e9).ToString("0.00", Fmt)} Gbit/s"
        : bps >= 1e6 ? $"{(bps / 1e6).ToString("0.0", Fmt)} Mbit/s"
        : $"{(bps / 1e3).ToString("0", Fmt)} kbit/s";

    private static string LinkSpeed(long bps) =>
        bps >= 1_000_000_000 ? $"{(bps / 1e9).ToString("0.#", Fmt)} Gbit/s" : $"{(bps / 1e6).ToString("0", Fmt)} Mbit/s";

    private static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} " + L.T("h", "t")
        : t.TotalHours >= 1 ? $"{t.Hours} " + L.T("h", "t") + $" {t.Minutes} m"
        : $"{t.Minutes} m";

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private sealed class FontSet : IDisposable
    {
        public readonly Font Big, Body, BodyBold, Small, SmallBold, Tiny, Icon, IconSmall, IconLarge;

        public FontSet(float scale)
        {
            var iconFamily = HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            Big = new Font("Segoe UI", 38 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            Body = new Font("Segoe UI", 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            BodyBold = new Font("Segoe UI Semibold", 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Small = new Font("Segoe UI", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            SmallBold = new Font("Segoe UI Semibold", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Tiny = new Font("Segoe UI", 10.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Icon = new Font(iconFamily, 13 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            IconSmall = new Font(iconFamily, 10 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            IconLarge = new Font(iconFamily, 20 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private static bool HasFont(string name)
        {
            using var fonts = new System.Drawing.Text.InstalledFontCollection();
            return fonts.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            foreach (var f in new[] { Big, Body, BodyBold, Small, SmallBold, Tiny, Icon, IconSmall, IconLarge }) f.Dispose();
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
