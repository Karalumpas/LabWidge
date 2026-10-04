using System.Drawing;

// Plugin rendering uses the shared widget drawing context.
internal sealed partial class DashboardForm
{
    internal float DrawNetwork(Graphics g, float x, float y, float w)
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
            foreach (var a in _net.Adapters.Skip(1).Where(a => s.ShowVirtualAdapters || a.Kind != NetworkMonitor.VirtualKind).Take(2))
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
        var max = Math.Max(Math.Max(_net.DownHistory.Tail(60).DefaultIfEmpty().Max(), _net.UpHistory.Tail(60).DefaultIfEmpty().Max()), 200_000);
        using (var pen = new Pen(_p.Line, Math.Max(1f, DpiScale)))
        {
            g.DrawLine(pen, chart.Left, chart.Bottom, chart.Right, chart.Bottom);
        }
        DrawSparkline(g, chart, _net.DownHistory, max, _p.Green, fill: true);
        DrawSparkline(g, chart, _net.UpHistory, max, _p.Blue, fill: false);
        _hits.Add(new Hit(chart, L.T($"Traffic over the last minute\nPeak: {Bps(max)}", $"Trafik det seneste minut\nTop: {Bps(max)}"), null));
        return y + U(30);
    }
}
