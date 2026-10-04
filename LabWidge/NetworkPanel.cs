using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

/// <summary>
/// The network window: the external IP, traffic and ping over time, and every adapter with address, gateway, DNS and speed.
/// Click an address to copy it.
/// </summary>
internal sealed class NetworkPanel : PopupPanel
{
    private static CultureInfo Fmt => L.Culture;

    private readonly NetworkMonitor _net;
    private readonly Func<string?> _externalIp;
    private readonly Func<(DateTime Time, string? Error)> _state;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private string? _copied;
    private DateTime _copiedAt;

    public NetworkPanel(NetworkMonitor net, Func<string?> externalIp, Func<(DateTime Time, string? Error)> state, AppSettings settings, Action saveSettings)
        : base("network", "", L.T("NETWORK", "NETVÆRK"), settings, saveSettings, 460)
    {
        _net = net;
        _externalIp = externalIp;
        _state = state;
        _tick.Tick += (_, _) => Invalidate();
        _tick.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }

    protected override string HeaderStatus => _net.PingMs is long ms ? $"ping {ms} ms" : L.T("no ping answer", "intet ping-svar");
    protected override Color? HeaderStatusColor => _net.PingMs == null ? P.Amber : null;

    private static string Bps(double bps) =>
        bps >= 1e9 ? $"{(bps / 1e9).ToString("0.00", Fmt)} Gbit/s"
        : bps >= 1e6 ? $"{(bps / 1e6).ToString("0.0", Fmt)} Mbit/s"
        : $"{(bps / 1e3).ToString("0", Fmt)} kbit/s";

    private static string Speed(long bps) =>
        bps >= 1_000_000_000 ? $"{(bps / 1e9).ToString("0.#", Fmt)} Gbit/s" : $"{(bps / 1e6).ToString("0", Fmt)} Mbit/s";

    private void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            _copied = text;
            _copiedAt = DateTime.Now;
            Invalidate();
        }
        catch
        {
            // The clipboard is busy; nothing to do
        }
    }

    /// <summary>An address that can be clicked to copy it.</summary>
    private float CopyRow(Graphics g, string key, string value, float x, float y, float w)
    {
        DrawText(g, key, F.Body, P.TextSecondary, x, y);
        var copied = _copied == value && (DateTime.Now - _copiedAt).TotalSeconds < 2;
        var shown = copied ? L.T("copied ✓", "kopieret ✓") : value;
        var vw = Measure(g, shown, F.BodyBold).Width;
        var rect = new RectangleF(x + w - vw - U(6), y - U(2), vw + U(12), U(21));
        var hovered = rect.Contains(Mouse);
        if (hovered) FillRound(g, rect, P.HoverBg, U(4));
        DrawText(g, shown, F.BodyBold, copied ? P.Green : hovered ? P.Blue : P.TextPrimary, x + w, y, right: true);
        AddHit(rect, L.T("Click to copy", "Klik for at kopiere"), () => Copy(value));
        return y + U(21);
    }

    protected override float RenderContent(Graphics g, float x, float y, float w)
    {
        // ---- External IP ----
        var (time, error) = _state();
        var ip = _externalIp();
        y = Subheading(g, L.T("INTERNET", "INTERNET"), x, y);
        y = ip != null ? CopyRow(g, L.T("External IP", "Ekstern IP"), ip, x, y, w)
                       : KeyValue(g, L.T("External IP", "Ekstern IP"), error != null ? L.T("not available", "ikke tilgængelig") : L.T("fetching…", "henter…"), x, y, w, P.Amber);
        if (error != null) y = Wrapped(g, "⚠ " + error, F.Small, P.Amber, x, y, w);
        else if (time != DateTime.MinValue)
        {
            DrawText(g, L.T("checked ", "tjekket ") + time.ToString("HH:mm"), F.Tiny, P.TextDim, x, y);
            y += U(18);
        }
        y += U(8);

        // ---- Traffic ----
        var down = _net.DownHistory.Tail(History.FiveMinutes);
        var up = _net.UpHistory.Tail(History.FiveMinutes);
        var max = Math.Max(Math.Max(down.DefaultIfEmpty().Max(), up.DefaultIfEmpty().Max()), 200_000);
        y = Subheading(g, L.T("TRAFFIC  ·  last 5 min", "TRAFIK  ·  sidste 5 min"), x, y);
        DrawText(g, "↓ " + Bps(_net.DownBps), F.BodyBold, P.Green, x, y);
        DrawText(g, "↑ " + Bps(_net.UpBps), F.BodyBold, P.Blue, x + U(150), y);
        DrawText(g, L.T("peak ", "top ") + Bps(max), F.Small, P.TextDim, x + w, y + U(1), right: true);
        y += U(22);
        var chart = new RectangleF(x, y, w, U(70));
        LineChart(g, chart, down, max, P.Green);
        LineChart(g, chart, up, max, P.Blue, fill: false);
        y += U(82);

        // ---- Ping ----
        var pings = _net.PingHistory.Tail(120);
        var ok = pings.Where(p => p >= 0).ToList();
        y = Subheading(g, $"PING  ·  {_net.PingTarget}  ·  " + L.T("last 10 min", "sidste 10 min"), x, y);
        DrawText(g, _net.PingMs is long ms ? $"{ms} ms" : L.T("no answer", "intet svar"), F.BodyBold, _net.PingMs == null ? P.Amber : P.TextPrimary, x, y);
        if (ok.Count > 0)
        {
            var lost = pings.Count - ok.Count;
            DrawText(g, L.T($"min {ok.Min():0}  ·  avg {ok.Average():0}  ·  max {ok.Max():0} ms", $"min {ok.Min():0}  ·  gns. {ok.Average():0}  ·  maks {ok.Max():0} ms")
                        + (lost > 0 ? L.T($"  ·  {lost} lost", $"  ·  {lost} tabt") : ""),
                     F.Small, lost > 0 ? P.Amber : P.TextDim, x + w, y + U(1), right: true);
        }
        y += U(22);
        var pingChart = new RectangleF(x, y, w, U(44));
        var pingMax = Math.Max(20, ok.DefaultIfEmpty().Max() * 1.2);
        LineChart(g, pingChart, pings.Select(p => p < 0 ? pingMax : p).ToList(), pingMax, P.Blue);
        // Lost answers are marked in red at the top
        for (var i = 0; i < pings.Count; i++)
        {
            if (pings[i] >= 0 || pings.Count < 2) continue;
            var px = pingChart.X + pingChart.Width * i / (pings.Count - 1);
            FillRound(g, new RectangleF(px - U(1.5f), pingChart.Y + U(1), U(3), U(6)), P.Red, U(1.5f));
        }
        y += U(56);

        // ---- Adapters ----
        y = Subheading(g, L.T("ADAPTERS", "ADAPTERE"), x, y);
        if (_net.Adapters.Count == 0) y = Wrapped(g, L.T("No connected adapters.", "Ingen forbundne adaptere."), F.Body, P.TextSecondary, x, y, w);
        foreach (var a in _net.Adapters.OrderByDescending(a => a.IsPrimary))
        {
            Dot(g, a.IsPrimary ? P.Green : P.TextDim, x + U(1), y + U(5));
            DrawText(g, Fit(g, a.Name, F.BodyBold, w * 0.6f), F.BodyBold, P.TextPrimary, x + U(16), y);
            var kind = a.Kind == NetworkMonitor.VirtualKind ? L.T("virtual", "virtuel") : a.Kind;
            DrawText(g, kind + (a.SpeedBps > 0 ? "  ·  " + Speed(a.SpeedBps) : "") + (a.IsPrimary ? L.T("  ·  primary", "  ·  primær") : ""),
                     F.Small, P.TextDim, x + w, y + U(1), right: true);
            y += U(22);
            y = CopyRow(g, "IPv4", $"{a.Ipv4}/{a.PrefixLength}", x + U(16), y, w - U(16));
            if (a.Gateway != null) y = CopyRow(g, "Gateway", a.Gateway, x + U(16), y, w - U(16));
            if (a.Dns.Length > 0) y = KeyValue(g, "DNS", string.Join(", ", a.Dns), x + U(16), y, w - U(16), tip: string.Join("\n", a.Dns));
            y += U(8);
        }
        return y;
    }
}
