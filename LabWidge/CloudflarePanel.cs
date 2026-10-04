using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

/// <summary>
/// The panel behind the Cloudflare section in the widget: each tunnel with the addresses it forwards and where to
/// (and whether they respond), all A records, and links to the Cloudflare dashboard.
/// </summary>
internal sealed class CloudflarePanel : PopupPanel
{
    private const int MaxRecords = 15;
    private static CultureInfo Fmt => L.Culture;

    private readonly CloudflareService _cf;
    private readonly ServiceMonitor _services;
    private readonly Action _saveSettings;
    private readonly Func<string?> _externalIp;

    public CloudflarePanel(CloudflareService cf, ServiceMonitor services, AppSettings settings, Action saveSettings, Func<string?> externalIp)
        : base("cloudflare", "", "CLOUDFLARE", settings, saveSettings, 400)
    {
        _cf = cf;
        _services = services;
        _saveSettings = saveSettings;
        _externalIp = externalIp;
        _cf.Updated += OnDataUpdated;
        _services.Updated += OnDataUpdated;
    }

    private void OnDataUpdated() => RequestRedraw();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cf.Updated -= OnDataUpdated;
            _services.Updated -= OnDataUpdated;
        }
        base.Dispose(disposing);
    }

    /// <summary>Right-clicking an address turns its check off or on again.</summary>
    private void ToggleIgnored(string host)
    {
        var ignored = Settings.IgnoredServices.ToList();
        if (ignored.RemoveAll(h => h.Equals(host, StringComparison.OrdinalIgnoreCase)) == 0) ignored.Add(host);
        Settings.IgnoredServices = ignored.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray();
        _saveSettings();
        Invalidate();
    }

    protected override string HeaderStatus =>
        (_cf.ZoneName != null ? _cf.ZoneName + "  ·  " : "")
        + (_cf.LastFetch == DateTime.MinValue ? L.T("fetching…", "henter…") : L.T("updated ", "opdateret ") + _cf.LastFetch.ToString("HH:mm"));

    protected override float RenderContent(Graphics g, float x, float y, float w)
    {

        // Tunnels
        y = Subheading(g, "TUNNELS", x, y);
        if (_cf.Tunnels.Count == 0)
        {
            var text = _cf.TunnelError == CloudflareService.MissingAccountId
                ? L.T("Fill in the Account ID under Settings → Cloudflare to see your tunnels.", "Udfyld Account ID under Indstillinger → Cloudflare for at se dine tunnels.")
                : _cf.TunnelError ?? (_cf.LastFetch == DateTime.MinValue ? L.T("Fetching…", "Henter…") : L.T("No tunnels on the account.", "Ingen tunnels på kontoen."));
            y = Wrapped(g, text, F.Small, _cf.TunnelError != null ? P.Amber : P.TextSecondary, x, y, w);
        }
        foreach (var t in _cf.Tunnels)
        {
            y = DrawTunnel(g, t, x, y, w);
        }
        if (_cf.Tunnels.Count > 0 && _cf.TunnelError != null)
        {
            y = Wrapped(g, "⚠ " + _cf.TunnelError, F.Small, P.Amber, x, y, w);
        }
        if (Settings.ServiceChecksEnabled && _cf.Tunnels.Any(t => t.Routes.Count > 0))
        {
            y = Wrapped(g, L.T("The dot shows whether the address responds. Right-click to turn the check off or on.", "Prikken viser om adressen svarer. Højreklik for at slå tjekket fra eller til."), F.Tiny, P.TextDim, x, y, w);
        }
        y += U(4);
        y = Divider(g, x, y, w);

        // DNS
        var ip = _externalIp();
        var records = _cf.ARecords;
        var managed = _cf.ManagedRecords(Settings);
        y = Subheading(g, "DNS  ·  A-POSTER", x, y);
        if (_cf.DnsError != null)
        {
            y = Wrapped(g, "⚠ " + _cf.DnsError, F.Small, P.Amber, x, y, w);
        }
        else if (records.Count == 0)
        {
            y = Wrapped(g, _cf.LastFetch == DateTime.MinValue ? L.T("Fetching…", "Henter…") : L.T("The zone has no A records.", "Zonen har ingen A-poster."), F.Small, P.TextSecondary, x, y, w);
        }
        foreach (var r in records.Take(MaxRecords))
        {
            var isManaged = managed.Contains(r);
            var current = ip != null && r.Content == ip;
            var color = !isManaged ? P.TextSecondary : current ? P.Green : P.Amber;
            var row = new RectangleF(x - U(6), y - U(3), w + U(12), U(22));
            var hovered = row.Contains(Mouse);
            if (hovered) FillRound(g, row, P.HoverBg, U(5));

            var vw = DrawText(g, r.Content, F.Small, color, x + w, y + U(1), right: true);
            DrawText(g, Fit(g, r.Name, F.Body, w - vw - U(12)), F.Body, hovered ? P.Blue : P.TextPrimary, x, y);

            var tip = $"{r.Name} → {r.Content}"
                      + (r.Proxied == true ? L.T("\nTraffic goes through Cloudflare (proxied)", "\nTrafikken går via Cloudflare (proxied)") : L.T("\nDNS only (not proxied)", "\nKun DNS (ikke proxied)"))
                      + (!isManaged ? L.T("\nNot updated by the widget", "\nOpdateres ikke af widgetten")
                         : current ? L.T("\nPoints to your current IP", "\nPeger på din nuværende IP") : L.T("\nDoes NOT point to your current IP", "\nPeger IKKE på din nuværende IP"))
                      + L.T("\nClick to open DNS in Cloudflare", "\nKlik for at åbne DNS i Cloudflare");
            AddHit(row, tip, () => Open(_cf.DashboardUrl("dns/records")));
            y += U(22);
        }
        if (records.Count > MaxRecords)
        {
            DrawText(g, L.T($"… and {records.Count - MaxRecords} more", $"… og {records.Count - MaxRecords} mere"), F.Small, P.TextDim, x, y);
            y += U(19);
        }
        y += U(4);
        y = Divider(g, x, y, w);

        y = LinkRow(g, L.T("Open tunnels in Cloudflare", "Åbn tunnels i Cloudflare"), _cf.TunnelsUrl(), x, y, w);
        return LinkRow(g, L.T("Open DNS in Cloudflare", "Åbn DNS i Cloudflare"), _cf.DashboardUrl("dns/records"), x, y, w);
    }

    private float DrawTunnel(Graphics g, CfTunnel t, float x, float y, float w)
    {
        var color = t.IsHealthy ? P.Green : t.IsDown ? P.Red : P.Amber;
        Dot(g, color, x + U(1), y + U(5), 9);
        var sw = DrawText(g, t.StatusText, F.Small, color, x + w, y + U(1), right: true);
        DrawText(g, Fit(g, t.Name, F.BodyBold, w - sw - U(28)), F.BodyBold, P.TextPrimary, x + U(18), y);
        y += U(20);

        var facts = new List<string>();
        if (t.Connections > 0) facts.Add(L.T($"{t.Connections} connections", $"{t.Connections} forbindelser") + (t.Colos.Length > 0 ? " via " + string.Join(", ", t.Colos) : ""));
        if (t.ClientVersion != null) facts.Add("cloudflared " + t.ClientVersion);
        if (t.ActiveSince is DateTime since) facts.Add(L.T("up since ", "oppe siden ") + since.ToString(L.T("d MMM HH:mm", "d. MMM HH:mm"), Fmt));
        if (facts.Count > 0)
        {
            var line = string.Join("  ·  ", facts);
            DrawText(g, Fit(g, line, F.Small, w - U(18)), F.Small, P.TextDim, x + U(18), y);
            AddHit(new RectangleF(x, y - U(2), w, U(18)), line + (t.OriginIp != null ? L.T("\nConnected from ", "\nForbundet fra ") + t.OriginIp : ""));
            y += U(19);
        }

        if (t.Routes.Count == 0)
        {
            DrawText(g, L.T("No public addresses found", "Ingen offentlige adresser fundet"), F.Small, P.TextDim, x + U(18), y);
            y += U(19);
        }
        foreach (var r in t.Routes)
        {
            var row = new RectangleF(x + U(12), y - U(3), w - U(6), U(21));
            var hovered = row.Contains(Mouse);
            if (hovered) FillRound(g, row, P.HoverBg, U(5));

            var checkable = r.Service == null || r.Service.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            var ignored = Settings.IgnoredServices.Contains(r.Hostname, StringComparer.OrdinalIgnoreCase);
            var status = checkable && !ignored ? _services.Get(r.Hostname) : null;
            if (Settings.ServiceChecksEnabled && checkable)
            {
                var dot = ignored || status == null ? P.Track : status.IsDown ? P.Red : status.Responded ? P.Green : P.Amber;
                Dot(g, dot, x + U(18), y + U(6), 6);
            }

            var nx = x + U(30);
            var nameW = Math.Min(Measure(g, r.Hostname, F.Body).Width, (w - U(30)) * 0.6f);
            DrawText(g, Fit(g, r.Hostname, F.Body, nameW), F.Body, ignored ? P.TextDim : hovered ? P.Blue : P.TextPrimary, nx, y);
            if (r.Service != null)
            {
                var room = x + w - nx - nameW - U(8);
                DrawText(g, Fit(g, "→ " + r.Service, F.Small, room), F.Small, P.TextSecondary, x + w, y + U(1.5f), right: true);
            }

            var url = "https://" + r.Hostname;
            var tip = (r.Service != null ? $"{r.Hostname} → {r.Service}\n" : $"{r.Hostname}\n")
                      + (!Settings.ServiceChecksEnabled || !checkable ? ""
                         : ignored ? L.T("Not checked (right-click to check again)\n", "Tjekkes ikke (højreklik for at tjekke igen)\n")
                         : status == null ? L.T("Not checked yet\n", "Ikke tjekket endnu\n")
                         : $"{status.Describe()} (" + L.T("", "kl. ") + $"{status.Checked:HH:mm})\n")
                      + L.T("Click to open ", "Klik for at åbne ") + url;
            var host = r.Hostname;
            AddHit(row, tip, () => Open(url), Settings.ServiceChecksEnabled && checkable ? () => ToggleIgnored(host) : null);
            y += U(21);
        }
        return y + U(6);
    }
}
