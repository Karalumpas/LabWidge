using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

/// <summary>
/// The window behind the "Home Assistant" line in the widget. Shows either a real Home Assistant dashboard
/// (when a dashboard path is set) or the list of switches for the selected entities.
/// </summary>
internal sealed class HomeAssistantPanel : PopupPanel
{
    private const float RowHeight = 26;

    private readonly HomeAssistantService _ha;
    private readonly string? _dashboardUrl;
    private WebView2? _web;
    private Label? _status;

    private bool IsDashboard => _dashboardUrl != null;

    public HomeAssistantPanel(HomeAssistantService ha, AppSettings settings, Action saveSettings)
        : base("ha", "", "HOME ASSISTANT", settings, saveSettings, 340)
    {
        _ha = ha;
        _dashboardUrl = BuildDashboardUrl(settings);

        // Older versions stored the dashboard's pin and size separately
        if (IsDashboard && !settings.SectionWindows.ContainsKey(Key))
        {
            State.Pinned = settings.HomeAssistantPanelPinned;
            State.Width = Math.Max((int)U(800), settings.HomeAssistantPanelWidth);
            State.Height = Math.Max((int)U(320), settings.HomeAssistantPanelHeight);
        }

        if (IsDashboard)
        {
            // At least 800 px: below 768 px the dashboard shows its mobile navigation at the bottom
            MinimumSize = new Size((int)U(800), (int)U(320));
            BuildWebView();
        }
        _ha.Updated += OnDataUpdated;
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

    protected override Size HostedDefaultSize => new((int)U(800), (int)U(760));

    protected override string HeaderStatus
    {
        get
        {
            var lit = _ha.Entities.Count(e => e.CanToggle && e.IsOn);
            return _ha.LastError != null ? L.T("⚠ no connection", "⚠ ingen forbindelse")
                : _ha.Entities.Count == 0 ? L.T("fetching…", "henter…")
                : lit == 0 ? L.T("all off", "alt slukket")
                : L.T($"{lit} on", $"{lit} tændt");
        }
    }

    protected override Color? HeaderStatusColor =>
        _ha.LastError != null || _ha.Entities.Any(e => e.CanToggle && e.IsOn) ? P.Amber : null;

    private void OnDataUpdated() => RequestRedraw();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ha.Updated -= OnDataUpdated;
            _web?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- Dashboard ----------

    private void BuildWebView()
    {
        var host = new Panel { BackColor = P.Bg };
        _status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = P.TextSecondary,
            BackColor = P.Bg,
            Font = new Font("Segoe UI", 9.5F),
            Text = L.T("Opening Home Assistant…", "Åbner Home Assistant…")
        };
        _web = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = P.Bg };
        host.Controls.Add(_status);
        host.Controls.Add(_web);
        _web.BringToFront();
        Host(host);
        Shown += async (_, _) => await InitWebViewAsync();
    }

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

            // Esc closes the window, also while the dashboard has keyboard focus. The listener is in the
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
                _status.ForeColor = P.Amber;
            }
        }
    }

    // ---------- List ----------

    protected override float RenderContent(Graphics g, float x, float y, float w)
    {
        var entities = _ha.Entities;
        if (entities.Count == 0)
        {
            return Wrapped(g, _ha.LastError ?? L.T("Fetching entities…", "Henter enheder…"), F.Body, P.TextSecondary, x, y, w);
        }

        foreach (var entity in entities) y = DrawRow(g, entity, x, y, w);

        if (_ha.LastError != null) y = Wrapped(g, "⚠ " + _ha.LastError, F.Small, P.Amber, x, y + U(4), w);
        return y;
    }

    private float DrawRow(Graphics g, HaEntity e, float x, float y, float w)
    {
        var pending = _ha.IsPending(e.EntityId);
        var clickable = e.CanToggle && !e.IsUnavailable && !pending;
        var row = new RectangleF(x - U(8), y - U(4), w + U(16), U(RowHeight));
        var hovered = row.Contains(Mouse);
        if (clickable && hovered) FillRound(g, row, P.HoverBg, U(6));

        var valueRight = x + w;
        if (e.CanToggle)
        {
            var knob = new RectangleF(x + w - U(32), y + U(2), U(32), U(17));
            DrawSwitch(g, knob, e.IsOn && !e.IsUnavailable, pending);
            valueRight = knob.X - U(10);
        }

        var value = pending ? L.T("sending…", "sender…") : e.Display();
        if (!pending && e.CanToggle && e.IsOn && e.BrightnessPercent is int percent) value += $" · {percent} %";
        var valueColor = e.IsUnavailable ? P.Amber
            : pending ? P.TextDim
            : e.CanToggle ? (e.IsOn ? P.TextPrimary : P.TextDim)
            : P.TextPrimary;
        var valueFont = e.CanToggle ? F.Small : F.BodyBold;
        var valueW = DrawText(g, value, valueFont, valueColor, valueRight, y + (e.CanToggle ? U(2.5f) : U(1)), right: true);

        var name = Fit(g, e.Name, F.Body, valueRight - valueW - U(10) - x);
        DrawText(g, name, F.Body, e.IsUnavailable ? P.TextDim : clickable && hovered ? P.Blue : P.TextPrimary, x, y + U(1));

        AddHit(row, TipFor(e), clickable ? () => _ = _ha.ToggleAsync(e.EntityId, Settings) : null);
        return y + U(RowHeight);
    }

    private string TipFor(HaEntity e) =>
        _ha.IsPending(e.EntityId) ? L.T("Sending to Home Assistant…", "Sender til Home Assistant…")
        : e.IsUnavailable ? e.EntityId + L.T(" is not responding", " svarer ikke")
        : e.CanToggle ? (e.IsOn ? L.T("Click to turn off", "Klik for at slukke") : L.T("Click to turn on", "Klik for at tænde")) + $"  ·  {e.EntityId}"
        : e.EntityId;

    private void DrawSwitch(Graphics g, RectangleF r, bool on, bool pending)
    {
        var track = on ? P.Green : P.Track;
        FillRound(g, r, pending ? Color.FromArgb(110, track) : track, r.Height / 2);

        var size = r.Height - U(4);
        var knob = new RectangleF(on ? r.Right - size - U(2) : r.X + U(2), r.Y + U(2), size, size);
        using var brush = new SolidBrush(on ? Color.White : P.TextDim);
        g.FillEllipse(brush, knob);
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
