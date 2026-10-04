using System.Drawing;

// Plugin rendering uses the shared widget drawing context.
internal sealed partial class DashboardForm
{
    internal float DrawCloudflare(Graphics g, float x, float y, float w)
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

        Action openPanel = () => SectionWindows.Toggle("cloudflare", Bounds);

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
}
