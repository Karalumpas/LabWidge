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
    private readonly Action _saveSettings;
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

        Services = new PluginServices
        {
            Price = el, System = sys, Network = net, Audio = audio, HomeAssistant = ha,
            Cloudflare = cf, ServiceMonitor = services, Proxmox = pve, ExternalIp = externalIp,
            NetworkState = _networkState, SwitchAudio = SwitchAudio, Redraw = Invalidate, WidgetVisible = () => Visible
        };

        _dragTimer.Tick += (_, _) => OnDragTick();
        SectionWindows.Settings = _settings;
        SectionWindows.Factory = CreateWindow;
        _el.Updated += OnDataUpdated;
        _ha.Updated += OnDataUpdated;
        _cf.Updated += OnDataUpdated;
        _services.Updated += OnDataUpdated;
        _pve.Updated += OnDataUpdated;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    internal PluginServices Services { get; }

    private float DpiScale => DeviceDpi / 96f;
    private float U(float v) => v * DpiScale;

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        SectionWindows.Settings = settings;
        SectionWindows.ApplySettings(settings);
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
            _dragTimer.Dispose();
            _drag?.Snapshot.Dispose();
            _ghost?.Dispose();
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
            if (UpdateTearOff()) return;
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

    /// <summary>One compact, interactive summary for each visible plugin.</summary>
    private SizeF RenderCompact(Graphics g)
    {
        float y = U(8);
        var width = U(LayoutWidth);
        foreach (var section in VisibleSections())
        {
            var plugin = WidgetPlugins.Find(section.Key)!;
            y = plugin.RenderCompact(this, g, U(Pad), y, width - U(Pad) * 2) + U(8);
        }
        if (y == U(8)) { DrawText(g, "LabWidge", _f.BodyBold, _p.TextPrimary, U(Pad), y); y += U(24); }
        return new SizeF(width, y);
    }

    internal float RenderPluginSummary(IWidgetPlugin plugin, Graphics g, float x, float y, float width)
    {
        var collapsed = Collapsed(plugin.Key);
        var section = _drawingSection;
        var summary = _drawingSummary;
        _drawingSection = plugin.Key;
        _drawingSummary = true;
        SetCollapsed(plugin.Key, true);
        try { return plugin.RenderExpanded(this, g, x, y, width); }
        finally { SetCollapsed(plugin.Key, collapsed); _drawingSection = section; _drawingSummary = summary; }
    }

    private (double? Price, PriceLevel Level) CurrentPrice()
    {
        var cur = _el.Current(DateTime.Now);
        if (cur == null) return (null, PriceLevel.Medium);
        var values = _el.HourlyConsumer(_settings).Select(h => h.Value).ToList();
        var total = _el.Consumer(cur.Time, cur.Spot, _settings);
        return (total, ElectricityPriceService.Level(total, values));
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



    /// <summary>The audio outputs the user chose to show – or all non-virtual ones if none are chosen.</summary>
    internal List<AudioDevice> VisibleAudioDevices()
    {
        var all = _audio.Devices;
        if (_settings.AudioDeviceIds is { } ids)
            return ids.Select(id => all.FirstOrDefault(d => d.Id == id)).OfType<AudioDevice>().ToList();
        return all.Where(d => !AudioService.HiddenByDefault(d)).ToList();
    }

    private string AudioName(AudioDevice d) =>
        _settings.AudioNames.TryGetValue(d.Id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : d.Name;

    internal static string AudioGlyph(AudioKind kind) => kind switch
    {
        AudioKind.Headphones => "",
        AudioKind.Headset => "",
        AudioKind.Display => "",
        AudioKind.Speakers or AudioKind.Digital => "",
        _ => ""
    };

    /// <summary>One button per audio output. A click makes it the Windows default device.</summary>


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


    private bool ShowsCloudflare => _settings.ShowCloudflare && _settings.HasCloudflare;

    /// <summary>Tunnels with status, whether the A records point to the current IP, and a shortcut to the details.</summary>


    /// <summary>The Proxmox host: CPU, RAM and storage, and how many machines are running. Click for the list of machines.</summary>


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
        var hovered = rect.Contains(_mouse) && _drag == null && key != null && SectionWindows.Supports(key);
        var rightW = 0f;
        if (right != null)
        {
            rightW = DrawText(g, Fit(g, right, _f.Small, Math.Max(U(25), w - U(74) - titleWidth - (hovered ? U(24) : 0))), _f.Small,
                rightColor ?? _p.TextDim, x + w - U(18), y, right: true);
        }
        if (hovered)
        {
            var icon = new RectangleF(x + w - U(18) - rightW - U(26), y - U(3), U(22), U(21));
            var over = icon.Contains(_mouse);
            if (over) FillRound(g, icon, _p.Track, U(4));
            DrawText(g, "\uE8A7", _f.IconSmall, over ? _p.TextPrimary : _p.TextSecondary, icon.X + U(6), icon.Y + U(5));
            _hits.Add(new Hit(icon, L.T("Open in a window – or drag the section out of the widget", "Åbn i et vindue – eller træk sektionen ud af widgetten"),
                () => SectionWindows.Toggle(key!, Bounds)));
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
        const int Shown = 60; // the last minute
        var values = h.Tail(Shown);
        if (values.Count < 2 || max <= 0) return;
        var step = r.Width / (Shown - 1);
        var offset = (Shown - values.Count) * step;
        var pts = new PointF[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var v = Math.Min(values[i], max) / max;
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
