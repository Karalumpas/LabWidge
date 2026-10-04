using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

/// <summary>
/// The electricity price window: the price now with its parts, a large chart of today and tomorrow with a price scale,
/// key figures per day and the cheapest and most expensive periods ahead.
/// </summary>
internal sealed class PricePanel : PopupPanel
{
    private static CultureInfo Fmt => L.Culture;

    private readonly ElectricityPriceService _el;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 30_000 };

    public PricePanel(ElectricityPriceService el, AppSettings settings, Action saveSettings)
        : base("price", "", L.T("ELECTRICITY", "ELPRIS"), settings, saveSettings, 560)
    {
        _el = el;
        _el.Updated += OnDataUpdated;
        // The "now" line and the current price follow the clock
        _clock.Tick += (_, _) => Invalidate();
        _clock.Start();
    }

    private void OnDataUpdated() => RequestRedraw();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _el.Updated -= OnDataUpdated;
            _clock.Dispose();
        }
        base.Dispose(disposing);
    }

    private PriceUnit Unit => Settings.PriceUnit;
    private string P0(double v) => Unit.Format(v, Fmt);

    protected override string? HeaderStatus
    {
        get
        {
            var area = Settings.PriceCountry?.AreaOrDefault(Settings.PriceArea);
            var where = area == null ? "" : area.ShortName.Length > 0 ? $"{area.Code} {area.ShortName}" : Settings.PriceCountry!.Name;
            var when = _el.LastError != null ? L.T("⚠ update failed", "⚠ opdatering fejlede")
                : _el.LastFetch == DateTime.MinValue ? L.T("fetching…", "henter…")
                : L.T("updated ", "opdateret ") + _el.LastFetch.ToString("HH:mm");
            return where.Length > 0 ? $"{where}  ·  {when}" : when;
        }
    }

    protected override Color? HeaderStatusColor => _el.LastError != null ? P.Amber : null;

    protected override float RenderContent(Graphics g, float x, float y, float w)
    {
        var s = Settings;
        var now = DateTime.Now;
        var cur = _el.Current(now);
        if (s.PriceCountry == null)
            return Wrapped(g, L.T("Choose your country under Settings → Electricity to see prices.", "Vælg dit land under Indstillinger → Elpris for at se priser."), F.Body, P.TextSecondary, x, y, w);
        if (cur == null)
            return Wrapped(g, _el.LastError != null ? L.T("Could not fetch prices – trying again. ", "Kunne ikke hente priser – prøver igen. ") + _el.LastError : L.T("Fetching prices…", "Henter priser…"),
                F.Body, _el.LastError != null ? P.Amber : P.TextSecondary, x, y, w);

        var hours = _el.HourlyConsumer(s);
        var values = hours.Select(h => h.Value).ToList();
        var bd = _el.Breakdown(cur.Time, cur.Spot, s);
        var level = ElectricityPriceService.Level(bd.Total, values);
        var color = P.Level(level);

        // ---- The price now ----
        var pw = DrawText(g, P0(bd.Total), F.Big, color, x - U(2), y - U(8));
        var px = x + pw + U(10);
        var levelText = LevelText(level);
        var ls = Measure(g, levelText, F.SmallBold);
        var pill = new RectangleF(px, y + U(4), ls.Width + U(14), ls.Height + U(5));
        FillRound(g, pill, Color.FromArgb(P.IsDark ? 48 : 36, color), pill.Height / 2);
        DrawText(g, levelText, F.SmallBold, color, pill.X + U(7), pill.Y + U(2.5f));
        DrawText(g, Unit.PerKwh, F.Small, P.TextSecondary, px, y + U(27));
        var what = s.PriceShowTotal ? L.T("total price", "samlet pris") : L.T("spot price", "spotpris");
        DrawText(g, $"{what}, " + (s.PriceInclVat ? L.T("incl. VAT", "inkl. moms") : L.T("excl. VAT", "ekskl. moms")), F.Tiny, P.TextDim, px, y + U(43));

        // The parts of the price, on the right
        var parts = new List<(string Label, double Value)> { ("Spot", bd.Spot) };
        if (s.PriceShowTotal && s.PriceCountry.Code == "DK")
        {
            parts.Add((L.T("Grid tariff", "Nettarif"), bd.NetTariff));
            parts.Add((L.T("Taxes", "Afgifter"), bd.StateCharges));
        }
        if (s.PriceShowTotal && s.SupplierAddOnOre != 0) parts.Add((s.PriceCountry.Code == "DK" ? L.T("Supplier", "Elselskab") : L.T("Add-on", "Tillæg"), bd.Supplier));
        if (s.PriceInclVat && bd.Total != 0) parts.Add((L.T("of which VAT", "heraf moms"), bd.Total - bd.Total / (1 + s.EffectiveVatPercent / 100)));
        var ry = y + U(1);
        foreach (var (label, value) in parts)
        {
            DrawText(g, P0(value), F.Small, P.TextSecondary, x + w, ry, right: true);
            DrawText(g, label, F.Small, P.TextDim, x + w - U(44), ry, right: true);
            ry += U(15);
        }
        y = Math.Max(y + U(62), ry + U(4));

        // Now and next
        var next = _el.Next(now);
        var length = next != null && next.Time > cur.Time ? next.Time - cur.Time : TimeSpan.FromMinutes(15);
        if (length > TimeSpan.FromHours(1)) length = TimeSpan.FromHours(1);
        var line = L.T("Now ", "Nu kl. ") + $"{cur.Time:HH:mm}–{cur.Time.Add(length):HH:mm}";
        if (next != null)
        {
            var nv = _el.Consumer(next.Time, next.Spot, s);
            line += L.T($"   ·   then {P0(nv)} {Unit.Symbol}", $"   ·   derefter {P0(nv)} {Unit.Symbol}")
                    + (nv > bd.Total + 0.5 ? " ↑" : nv < bd.Total - 0.5 ? " ↓" : " →");
        }
        DrawText(g, line, F.Small, P.TextSecondary, x, y);
        y += U(26);

        // ---- Chart ----
        y = DrawChart(g, hours, values, now, x, y, w);

        // ---- Key figures ----
        y = Divider(g, x, y + U(4), w);
        var colW = w / 2;
        var today = hours.Where(h => h.Hour.Date == now.Date).Select(h => h.Value).ToList();
        var tomorrow = hours.Where(h => h.Hour.Date == now.Date.AddDays(1)).Select(h => h.Value).ToList();
        DayFigures(g, L.T("TODAY", "I DAG"), today, x, y, colW - U(12));
        if (tomorrow.Count > 0) DayFigures(g, L.T("TOMORROW", "I MORGEN"), tomorrow, x + colW + U(12), y, colW - U(12));
        else Wrapped(g, s.PriceCountry.Code == "DK" ? L.T("Tomorrow's prices arrive around 13:00.", "Morgendagens priser kommer ca. kl. 13.")
                                                    : L.T("Tomorrow's prices arrive in the early afternoon.", "Morgendagens priser kommer først på eftermiddagen."),
                     F.Small, P.TextDim, x + colW + U(12), y + U(19), colW - U(12));
        y += U(84);

        // ---- Periods ahead ----
        y = Divider(g, x, y, w);
        y = Subheading(g, L.T("CHEAPEST AND MOST EXPENSIVE AHEAD", "BILLIGST OG DYREST FREMOVER"), x, y);
        var curHour = now.Date.AddHours(now.Hour);
        var future = hours.Where(h => h.Hour >= curHour).ToList();
        foreach (var n in new[] { 1, 3, 5 })
            y = PeriodRow(g, Best(future, n, cheapest: true), n, L.T($"Cheapest {n} h", $"Billigste {n} t"), P.Green, now, x, y, w);
        y = PeriodRow(g, Best(future, 3, cheapest: false), 3, L.T("Most expensive 3 h", "Dyreste 3 t"), P.Red, now, x, y, w);

        if (s.PriceShowTotal && _el.TariffError != null)
            y = Wrapped(g, L.T("⚠ Tariffs could not be fetched – the price may be incomplete.", "⚠ Tariffer kunne ikke hentes – prisen kan være ufuldstændig."), F.Small, P.Amber, x, y + U(4), w);
        return y;
    }

    private static string LevelText(PriceLevel level) => level switch
    {
        PriceLevel.Cheap => L.T("Cheap", "Billig"),
        PriceLevel.Medium => L.T("Medium", "Middel"),
        _ => L.T("Expensive", "Dyr")
    };

    private float DrawChart(Graphics g, IReadOnlyList<(DateTime Hour, double Value)> hours, List<double> values, DateTime now, float x, float y, float w)
    {
        var labelW = U(34);
        var chart = new RectangleF(x + labelW, y + U(4), w - labelW, U(170));
        double lo = Math.Min(0, values.Min()), hi = Math.Max(values.Max(), lo + 1);
        var step = NiceStep((hi - lo) / 4);
        lo = Math.Floor(lo / step) * step;
        hi = Math.Ceiling(hi / step) * step;
        float Y(double v) => chart.Bottom - (float)((v - lo) / (hi - lo)) * chart.Height;

        // Price scale
        using (var grid = new Pen(Color.FromArgb(P.IsDark ? 60 : 90, P.Line), Math.Max(1f, DpiScale)) { DashStyle = DashStyle.Dot })
        {
            for (var v = lo; v <= hi + step / 2; v += step)
            {
                var gy = Y(v);
                g.DrawLine(grid, chart.X, gy, chart.Right, gy);
                DrawText(g, v.ToString("0", Fmt), F.Tiny, P.TextDim, chart.X - U(6), gy - U(7), right: true);
            }
        }

        var n = hours.Count;
        var slot = chart.Width / n;
        var gap = slot > U(6) ? U(2) : U(0.8f);
        var curHour = now.Date.AddHours(now.Hour);
        var y0 = Y(0);
        for (var i = 0; i < n; i++)
        {
            var (hour, value) = hours[i];
            var top = Y(value);
            var rect = new RectangleF(chart.X + i * slot + gap / 2, Math.Min(top, y0), Math.Max(1f, slot - gap), Math.Max(U(2), Math.Abs(y0 - top)));
            var hovered = new RectangleF(chart.X + i * slot, chart.Y, slot, chart.Height).Contains(Mouse);
            var alpha = hovered ? 255 : hour < curHour ? 80 : hour == curHour ? 255 : 210;
            FillRound(g, rect, Color.FromArgb(alpha, P.Level(ElectricityPriceService.Level(value, values))), Math.Min(U(2), rect.Width / 2));
            AddHit(new RectangleF(chart.X + i * slot, chart.Y, slot, chart.Height + U(20)),
                hour.ToString(L.T("dddd d MMM", "dddd d. MMM"), Fmt) + L.T(" ", " kl. ") + $"{hour:HH}–{hour.AddHours(1):HH}\n{P0(value)} {Unit.PerKwh}  ·  {LevelText(ElectricityPriceService.Level(value, values)).ToLower(Fmt)}");

            if (hour.Hour % 3 == 0)
            {
                var tomorrow = hour.Hour == 0 && i > 0;
                DrawText(g, tomorrow ? L.T("tomorrow", "i morgen") : hour.ToString("HH"), F.Tiny, tomorrow ? P.TextSecondary : P.TextDim, chart.X + i * slot, chart.Bottom + U(5));
                if (tomorrow)
                {
                    using var dash = new Pen(Color.FromArgb(90, P.TextSecondary), Math.Max(1f, DpiScale)) { DashStyle = DashStyle.Dash };
                    g.DrawLine(dash, chart.X + i * slot - gap / 2, chart.Y - U(2), chart.X + i * slot - gap / 2, chart.Bottom + U(18));
                }
            }
        }

        // Now
        var nowIndex = hours.ToList().FindIndex(h => h.Hour == curHour);
        if (nowIndex >= 0)
        {
            var nx = chart.X + (nowIndex + now.Minute / 60f) * slot;
            using var pen = new Pen(P.TextPrimary, Math.Max(1f, U(1.3f)));
            g.DrawLine(pen, nx, chart.Y - U(4), nx, chart.Bottom);
            DrawText(g, L.T("now", "nu"), F.Tiny, P.TextPrimary, nx + U(3), chart.Y - U(6));
        }
        DrawText(g, Unit.Symbol, F.Tiny, P.TextDim, x, chart.Y - U(16));
        return chart.Bottom + U(26);
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var n = raw / mag;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * mag;
    }

    private void DayFigures(Graphics g, string title, List<double> values, float x, float y, float w)
    {
        Subheading(g, title, x, y);
        y += U(19);
        if (values.Count == 0) return;
        KeyValue(g, L.T("Lowest", "Laveste"), $"{P0(values.Min())} {Unit.Symbol}", x, y, w, P.Green);
        KeyValue(g, L.T("Average", "Gennemsnit"), $"{P0(values.Average())} {Unit.Symbol}", x, y + U(21), w);
        KeyValue(g, L.T("Highest", "Højeste"), $"{P0(values.Max())} {Unit.Symbol}", x, y + U(42), w, P.Red);
    }

    /// <summary>The cheapest (or most expensive) run of <paramref name="n"/> whole hours.</summary>
    private static (DateTime Start, double Average)? Best(List<(DateTime Hour, double Value)> hours, int n, bool cheapest)
    {
        if (hours.Count < n) return null;
        (DateTime, double)? best = null;
        for (var i = 0; i <= hours.Count - n; i++)
        {
            if (hours[i + n - 1].Hour - hours[i].Hour != TimeSpan.FromHours(n - 1)) continue; // a gap in the data
            var avg = hours.Skip(i).Take(n).Average(h => h.Value);
            if (best == null || (cheapest ? avg < best.Value.Item2 : avg > best.Value.Item2)) best = (hours[i].Hour, avg);
        }
        return best;
    }

    private float PeriodRow(Graphics g, (DateTime Start, double Average)? period, int n, string label, Color color, DateTime now, float x, float y, float w)
    {
        DrawText(g, label, F.Body, P.TextSecondary, x, y);
        if (period is not { } p)
        {
            DrawText(g, "–", F.Body, P.TextDim, x + w, y, right: true);
            return y + U(22);
        }
        var day = p.Start.Date == now.Date ? L.T("today", "i dag") : L.T("tomorrow", "i morgen");
        var when = L.T($"{day} {p.Start:HH}–{p.Start.AddHours(n):HH}", $"{day} kl. {p.Start:HH}–{p.Start.AddHours(n):HH}");
        var starts = p.Start - now;
        var soon = starts > TimeSpan.Zero && starts < TimeSpan.FromHours(12)
            ? L.T($"  ·  in {(int)starts.TotalHours} h {starts.Minutes} min", $"  ·  om {(int)starts.TotalHours} t {starts.Minutes} min")
            : starts <= TimeSpan.Zero ? L.T("  ·  now", "  ·  nu") : "";
        var vw = DrawText(g, $"{P0(p.Average)} {Unit.Symbol}", F.BodyBold, color, x + w, y, right: true);
        DrawText(g, when + soon, F.Body, P.TextPrimary, x + w - vw - U(14), y, right: true);
        return y + U(22);
    }
}
