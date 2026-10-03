using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

/// <summary>
/// The panel behind the "Home Assistant" line in the widget. Shows either a real Home Assistant dashboard
/// (when a dashboard path is set) or the list of switches for the selected entities.
/// </summary>
internal sealed class HomeAssistantPanel : Form
{
    private const float LogicalWidth = 320;
    private const float Pad = 16;
    private const float RowHeight = 26;
    private const TextFormatFlags TF = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private static HomeAssistantPanel? _open;
    private static DateTime _closedAt = DateTime.MinValue;

    private readonly HomeAssistantService _ha;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly string? _dashboardUrl;
    private readonly ToolTip _tip = new() { UseAnimation = false, UseFading = false };
    private readonly List<(RectangleF Rect, HaEntity Entity)> _rows = new();

    private WebView2? _web;
    private Label? _status;
    private Label? _pin;
    private Panel? _bar;
    private bool _pinned;
    private Palette _p;
    private Fonts _f;
    private Point _mouse = new(-1, -1);
    private string? _hoverTip;

    private bool IsDashboard => _dashboardUrl != null;

    /// <summary>True while the panel is open, so the state keeps being refreshed.</summary>
    public static bool IsOpen => _open is { IsDisposed: false, Visible: true };

    /// <summary>Opens the panel next to the widget – or closes it if it is already showing.</summary>
    public static void Toggle(HomeAssistantService ha, AppSettings settings, Action saveSettings, Rectangle near)
    {
        if (IsOpen)
        {
            _open!.Close();
            return;
        }

        // A click on the widget first makes the panel lose focus and close; without this
        // grace period the same click would open it again straight away
        if ((DateTime.Now - _closedAt).TotalMilliseconds < 250) return;

        var panel = new HomeAssistantPanel(ha, settings, saveSettings);
        _open = panel;
        panel.ShowNear(near);
    }

    public static void CloseIfOpen()
    {
        if (IsOpen) _open!.Close();
    }

    private HomeAssistantPanel(HomeAssistantService ha, AppSettings settings, Action saveSettings)
    {
        _ha = ha;
        _settings = settings;
        _saveSettings = saveSettings;
        _dashboardUrl = BuildDashboardUrl(settings);
        _p = Palette.For(settings.Theme);

        Text = "Home Assistant";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = _p.Bg;
        TopMost = true;
        Icon = AppIconProvider.GetIcon();
        _f = new Fonts(DpiScale);

        if (IsDashboard)
        {
            // No title bar – like the list. Closed with Esc or by clicking outside.
            FormBorderStyle = FormBorderStyle.None;
            // At least 800 px: below 768 px the dashboard shows its mobile navigation at the bottom
            MinimumSize = new Size((int)U(800), (int)U(320));
            ClientSize = new Size(
                Math.Max((int)U(800), settings.HomeAssistantPanelWidth),
                Math.Max((int)U(320), settings.HomeAssistantPanelHeight));
            BuildWebView();
        }
        else
        {
            FormBorderStyle = FormBorderStyle.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            _ha.Updated += OnDataUpdated;
        }
    }

    private static string? BuildDashboardUrl(AppSettings settings)
    {
        var path = settings.HomeAssistantDashboardPath?.Trim();
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(settings.HomeAssistantUrl)) return null;

        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        return HomeAssistantService.Normalize(settings.HomeAssistantUrl) + "/" + path.TrimStart('/');
    }

    private float DpiScale => DeviceDpi / 96f;
    private float U(float v) => v * DpiScale;

    // ---------- Dashboard ----------

    private void BuildWebView()
    {
        _status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = _p.TextSecondary,
            BackColor = _p.Bg,
            Font = new Font("Segoe UI", 9.5F),
            Text = L.T("Opening Home Assistant…", "Åbner Home Assistant…")
        };
        Controls.Add(_status);

        _web = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = _p.Bg };
        Controls.Add(_web);
        _web.BringToFront();

        // A narrow strip with a pin: when pinned, the panel does not close when you click elsewhere.
        // The strip can also be dragged to move the panel.
        _pinned = _settings.HomeAssistantPanelPinned;
        var iconFamily = Fonts.HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        _pin = new Label
        {
            Dock = DockStyle.Right,
            Width = (int)U(34),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(iconFamily, 11f),
            Cursor = Cursors.Hand,
            BackColor = _p.Bg
        };
        _bar = new Panel { Dock = DockStyle.Top, Height = (int)U(22), BackColor = _p.Bg };
        _bar.Controls.Add(_pin);
        Controls.Add(_bar);
        _bar.SendToBack();
        UpdatePin();
        _pin.Click += (_, _) =>
        {
            _pinned = !_pinned;
            _settings.HomeAssistantPanelPinned = _pinned;
            _saveSettings();
            UpdatePin();
        };
        _bar.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
        };

        Shown += async (_, _) => await InitWebViewAsync();
    }

    private void UpdatePin()
    {
        _pin!.Text = _pinned ? "" : "";
        _pin.ForeColor = _pinned ? _p.Blue : _p.TextDim;
        _tip.SetToolTip(_pin, _pinned ? L.T("Pinned – click to unpin", "Fastgjort – klik for at frigøre") : L.T("Pin so the panel stays open", "Fastgør, så panelet bliver åbent"));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private async Task InitWebViewAsync()
    {
        try
        {
            // A profile folder of its own, so the Home Assistant login is remembered between starts
            var profile = WebViewProfile.Path;
            Directory.CreateDirectory(profile);

            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            await _web!.EnsureCoreWebView2Async(environment);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            // Esc closes the panel, also while the dashboard has keyboard focus. The listener is in the
            // bubbling phase, so Home Assistant's own dialogs get Escape first and can stop it.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "document.addEventListener('keydown', function (e) {" +
                "  if (e.key === 'Escape') window.chrome.webview.postMessage('close');" +
                "});");
            core.WebMessageReceived += (_, e) =>
            {
                if (e.TryGetWebMessageAsString() == "close") BeginInvoke(new Action(Close));
            };

            _web.Source = new Uri(_dashboardUrl!);
            _web.NavigationCompleted += (_, _) =>
            {
                _web.Visible = true;
                if (_status != null) _status.Visible = false;
            };
            Logger.Info($"Home Assistant dashboard opened: {_dashboardUrl}");
        }
        catch (Exception ex)
        {
            Logger.Error($"The dashboard could not be shown: {ex.Message}");
            if (_status != null)
            {
                _status.Text = L.T("The dashboard could not be shown.\n\n", "Dashboardet kunne ikke vises.\n\n") + ex.Message +
                               L.T("\n\nMicrosoft Edge WebView2 Runtime must be installed.", "\n\nMicrosoft Edge WebView2 Runtime skal være installeret.");
                _status.ForeColor = _p.Amber;
            }
        }
    }

    // ---------- Window ----------

    private void OnDataUpdated()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => { FitSize(); Invalidate(); }));
    }

    private void ShowNear(Rectangle widget)
    {
        if (!IsDashboard) FitSize();

        // To the left of the widget if there is room, otherwise to its right
        var screen = Screen.FromRectangle(widget).WorkingArea;
        var left = widget.Left - Width - (int)U(8);
        if (left < screen.Left) left = widget.Right + (int)U(8);
        if (left + Width > screen.Right) left = screen.Right - Width - (int)U(8);

        var top = Math.Min(widget.Top, screen.Bottom - Height - (int)U(8));
        Location = new Point(Math.Max(screen.Left, left), Math.Max(screen.Top, top));

        Show();

        // The click on the widget gives the widget focus again once the handler is done. Focus is
        // therefore taken afterwards – otherwise neither Esc nor "close on losing focus" works.
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            Activate();
            SetForegroundWindow(Handle);
            if (!IsDashboard) SetFocus(Handle);
        }));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_pinned) Close();
    }

    /// <summary>
    /// Escape is caught in the message loop itself. The list has no child controls, so neither
    /// OnKeyDown nor ProcessCmdKey is reliable here.
    /// </summary>
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (IsDashboard && WindowState == FormWindowState.Normal)
        {
            _settings.HomeAssistantPanelWidth = ClientSize.Width;
            _settings.HomeAssistantPanelHeight = ClientSize.Height;
            _saveSettings();
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closedAt = DateTime.Now;
        if (ReferenceEquals(_open, this)) _open = null;
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!IsDashboard) _ha.Updated -= OnDataUpdated;
            _web?.Dispose();
            _tip.Dispose();
            _f.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            cp.ExStyle |= 0x80;                            // WS_EX_TOOLWINDOW: no Alt+Tab / taskbar
            return cp;
        }
    }

    // ---------- Mouse (list only) ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsDashboard) return;

        _mouse = e.Location;
        var hit = _rows.FirstOrDefault(r => r.Rect.Contains(_mouse));
        var tip = hit.Entity == null ? null : TipFor(hit.Entity);
        if (tip != _hoverTip)
        {
            _hoverTip = tip;
            if (tip == null) _tip.Hide(this);
            else _tip.Show(tip, this, e.Location.X + 14, e.Location.Y + 20, 4000);
        }
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (IsDashboard) return;

        _mouse = new Point(-1, -1);
        _hoverTip = null;
        _tip.Hide(this);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (IsDashboard || e.Button != MouseButtons.Left) return;

        foreach (var (rect, entity) in _rows)
        {
            if (!rect.Contains(e.Location)) continue;
            if (entity.CanToggle && !entity.IsUnavailable && !_ha.IsPending(entity.EntityId))
            {
                _ = _ha.ToggleAsync(entity.EntityId, _settings);
            }
            return;
        }
    }

    private string TipFor(HaEntity e) =>
        _ha.IsPending(e.EntityId) ? L.T("Sending to Home Assistant…", "Sender til Home Assistant…")
        : e.IsUnavailable ? e.EntityId + L.T(" is not responding", " svarer ikke")
        : e.CanToggle ? (e.IsOn ? L.T("Click to turn off", "Klik for at slukke") : L.T("Click to turn on", "Klik for at tænde")) + $"  ·  {e.EntityId}"
        : e.EntityId;

    // ---------- Drawing (list only) ----------

    private void FitSize()
    {
        if (IsDashboard) return;

        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        var size = Render(g);
        ClientSize = new Size((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (IsDashboard)
        {
            base.OnPaint(e);
            return;
        }

        var g = e.Graphics;
        g.Clear(_p.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var size = Render(g);
        if (Math.Abs(ClientSize.Height - size.Height) > 1)
        {
            ClientSize = new Size(ClientSize.Width, (int)Math.Ceiling(size.Height));
            Invalidate();
            return;
        }

        using var border = new Pen(_p.Line, Math.Max(1f, DpiScale));
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private SizeF Render(Graphics g)
    {
        _rows.Clear();
        var width = U(LogicalWidth);
        float x = U(Pad), w = width - U(Pad) * 2, y = U(14);

        var entities = _ha.Entities;
        var lit = entities.Count(e => e.CanToggle && e.IsOn);

        DrawText(g, "", _f.Icon, _p.TextSecondary, x, y + U(1));
        DrawText(g, "HOME ASSISTANT", _f.SmallBold, _p.TextSecondary, x + U(21), y);
        var summary = _ha.LastError != null ? L.T("⚠ no connection", "⚠ ingen forbindelse")
            : entities.Count == 0 ? L.T("fetching…", "henter…")
            : lit == 0 ? L.T("all off", "alt slukket")
            : L.T($"{lit} on", $"{lit} tændt");
        DrawText(g, summary, _f.Small, _ha.LastError != null ? _p.Amber : lit > 0 ? _p.Amber : _p.TextSecondary,
                 x + w, y, right: true);
        y += U(22);

        using (var pen = new Pen(_p.Line, Math.Max(1f, DpiScale)))
        {
            g.DrawLine(pen, x, y, x + w, y);
        }
        y += U(10);

        if (entities.Count == 0)
        {
            DrawText(g, _ha.LastError ?? L.T("Fetching entities…", "Henter enheder…"), _f.Body, _p.TextSecondary, x, y);
            return new SizeF(width, y + U(30));
        }

        foreach (var entity in entities)
        {
            y = DrawRow(g, entity, x, y, w);
        }

        y += U(4);
        DrawText(g, L.T("Click outside – or press Esc – to close", "Klik ved siden af – eller Esc – for at lukke"), _f.Tiny, _p.TextDim, x, y);
        return new SizeF(width, y + U(20));
    }

    private float DrawRow(Graphics g, HaEntity e, float x, float y, float w)
    {
        var pending = _ha.IsPending(e.EntityId);
        var clickable = e.CanToggle && !e.IsUnavailable && !pending;
        var row = new RectangleF(x - U(8), y - U(4), w + U(16), U(RowHeight));
        var hovered = row.Contains(_mouse);
        if (clickable && hovered) FillRound(g, row, _p.HoverBg, U(6));

        var valueRight = x + w;
        if (e.CanToggle)
        {
            var knob = new RectangleF(x + w - U(32), y + U(2), U(32), U(17));
            DrawSwitch(g, knob, e.IsOn && !e.IsUnavailable, pending);
            valueRight = knob.X - U(10);
        }

        var value = pending ? L.T("sending…", "sender…") : e.Display();
        if (!pending && e.CanToggle && e.IsOn && e.BrightnessPercent is int percent) value += $" · {percent} %";
        var valueColor = e.IsUnavailable ? _p.Amber
            : pending ? _p.TextDim
            : e.CanToggle ? (e.IsOn ? _p.TextPrimary : _p.TextDim)
            : _p.TextPrimary;
        var valueFont = e.CanToggle ? _f.Small : _f.BodyBold;
        var valueW = DrawText(g, value, valueFont, valueColor, valueRight, y + (e.CanToggle ? U(2.5f) : U(1)), right: true);

        var name = e.Name;
        var room = valueRight - valueW - U(10) - x;
        if (Measure(g, name, _f.Body).Width > room)
        {
            while (name.Length > 3 && Measure(g, name + "…", _f.Body).Width > room) name = name[..^1];
            name = name.TrimEnd() + "…";
        }
        DrawText(g, name, _f.Body, e.IsUnavailable ? _p.TextDim : clickable && hovered ? _p.Blue : _p.TextPrimary, x, y + U(1));

        _rows.Add((row, e));
        return y + U(RowHeight);
    }

    private void DrawSwitch(Graphics g, RectangleF r, bool on, bool pending)
    {
        var track = on ? _p.Green : _p.Track;
        FillRound(g, r, pending ? Color.FromArgb(110, track) : track, r.Height / 2);

        var size = r.Height - U(4);
        var knob = new RectangleF(on ? r.Right - size - U(2) : r.X + U(2), r.Y + U(2), size, size);
        using var brush = new SolidBrush(on ? Color.White : _p.TextDim);
        g.FillEllipse(brush, knob);
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

    private sealed class Fonts : IDisposable
    {
        public readonly Font Body, BodyBold, Small, SmallBold, Tiny, Icon;

        public Fonts(float scale)
        {
            var iconFamily = HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            Body = new Font("Segoe UI", 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            BodyBold = new Font("Segoe UI Semibold", 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Small = new Font("Segoe UI", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            SmallBold = new Font("Segoe UI Semibold", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Tiny = new Font("Segoe UI", 10.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Icon = new Font(iconFamily, 13 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public static bool HasFont(string name)
        {
            using var fonts = new System.Drawing.Text.InstalledFontCollection();
            return fonts.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            foreach (var f in new[] { Body, BodyBold, Small, SmallBold, Tiny, Icon }) f.Dispose();
        }
    }
}

/// <summary>
/// The Home Assistant panel's browser profile (login, cookies). It lives outside the installation folder, which is emptied
/// on every update – otherwise you would have to sign in again after every new version.
/// </summary>
internal static class WebViewProfile
{
    public static string DataDir { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LabWidgeData");

    public static string Path { get; } = System.IO.Path.Combine(DataDir, "webview");
}
