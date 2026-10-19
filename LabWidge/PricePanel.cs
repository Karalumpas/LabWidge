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
        var periods = _el.ConsumerPeriods(s);
        var cheapest3 = PriceWindows.Cheapest(periods, now, TimeSpan.FromHours(3));
        y = DrawChart(g, periods, values, cheapest3, now, x, y, w);

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
        foreach (var n in new[] { 1, 3, 5 })
            y = PeriodRow(g, n == 3 ? cheapest3 : PriceWindows.Cheapest(periods, now, TimeSpan.FromHours(n)), L.T($"Cheapest {n} h", $"Billigste {n} t"), P.Green, now, x, y, w);
        y = PeriodRow(g, PriceWindows.Cheapest(periods, now, TimeSpan.FromHours(3), cheapest: false), L.T("Most expensive 3 h", "Dyreste 3 t"), P.Red, now, x, y, w);

        y = DrawAppliances(g, periods, now, x, y, w);
        y = DrawRules(g, periods, values, now, x, y, w);
        y = DrawHistory(g, now, x, y, w);

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

    private float DrawChart(Graphics g, IReadOnlyList<PricePeriod> periods, List<double> values, PriceWindow? cheapest, DateTime now, float x, float y, float w)
    {
        var labelW = U(34);
        var chart = new RectangleF(x + labelW, y + U(20), w - labelW, U(170));
        if (periods.Count == 0) return chart.Bottom + U(26);
        double lo = Math.Min(0, periods.Min(p => p.Value)), hi = Math.Max(periods.Max(p => p.Value), lo + 1);
        var step = NiceStep((hi - lo) / 4);
        lo = Math.Floor(lo / step) * step;
        hi = Math.Ceiling(hi / step) * step;
        float Y(double v) => chart.Bottom - (float)((v - lo) / (hi - lo)) * chart.Height;

        // Price scale
        using (var grid = new Pen(Color.FromArgb(P.IsDark ? 150 : 200, P.Line), Math.Max(1f, DpiScale)) { DashStyle = DashStyle.Dot })
        {
            for (var v = lo; v <= hi + step / 2; v += step)
            {
                var gy = Y(v);
                g.DrawLine(grid, chart.X, gy, chart.Right, gy);
                DrawText(g, v.ToString("0", Fmt), F.Tiny, P.TextDim, chart.X - U(6), gy - U(7), right: true);
            }
        }

        // The bars are placed by time, so quarter-hours and whole hours share the same axis
        var first = periods[0].Start;
        var total = (float)(periods[^1].End - first).TotalMinutes;
        float X(DateTime t) => chart.X + (float)(t - first).TotalMinutes / total * chart.Width;
        var hourWidth = X(first.AddHours(1)) - chart.X;
        var quarter = periods[0].Length < TimeSpan.FromHours(1);
        var gap = quarter ? (hourWidth > U(16) ? U(0.8f) : U(0.3f)) : (hourWidth > U(6) ? U(2) : U(0.8f));

        // The cheapest 3 hours behind the bars
        if (cheapest is { } best)
        {
            var band = new RectangleF(X(best.Start), chart.Y - U(4), X(best.End) - X(best.Start), chart.Height + U(4));
            FillRound(g, band, Color.FromArgb(P.IsDark ? 34 : 26, P.Green), U(3));
        }

        var y0 = Y(0);
        foreach (var p in periods)
        {
            float left = X(p.Start), right = X(p.End);
            var top = Y(p.Value);
            var rect = new RectangleF(left + gap / 2, Math.Min(top, y0), Math.Max(1f, right - left - gap), Math.Max(U(2), Math.Abs(y0 - top)));
            var hovered = new RectangleF(left, chart.Y, right - left, chart.Height).Contains(Mouse);
            var alpha = hovered ? 255 : p.End <= now ? 80 : p.Start <= now ? 255 : 210;
            var level = ElectricityPriceService.Level(p.Value, values);
            FillRound(g, rect, Color.FromArgb(alpha, P.Level(level)), Math.Min(U(2), rect.Width / 2));
            var inCheapest = cheapest is { } c && p.Start >= c.Start && p.End <= c.End;
            AddHit(new RectangleF(left, chart.Y, right - left, chart.Height + U(20)),
                p.Start.ToString(L.T("dddd d MMM", "dddd d. MMM"), Fmt) + L.T(" ", " kl. ") + $"{p.Start:HH:mm}–{p.End:HH:mm}\n{P0(p.Value)} {Unit.PerKwh}  ·  {LevelText(level).ToLower(Fmt)}"
                + (inCheapest ? L.T("\nIn the cheapest 3 hours", "\nI de billigste 3 timer") : ""));

            if (p.Start.Minute == 0 && p.Start.Hour % 3 == 0 && !(p.Start.Hour == 3 && p.Start.Date > now.Date)) // "tomorrow" needs the room of 03
            {
                var tomorrow = p.Start.Hour == 0 && p.Start > first;
                DrawText(g, tomorrow ? L.T("tomorrow", "i morgen") : p.Start.ToString("HH"), F.Tiny, tomorrow ? P.TextSecondary : P.TextDim, left, chart.Bottom + U(5));
                if (tomorrow)
                {
                    using var dash = new Pen(Color.FromArgb(90, P.TextSecondary), Math.Max(1f, DpiScale)) { DashStyle = DashStyle.Dash };
                    g.DrawLine(dash, left - gap / 2, chart.Y - U(2), left - gap / 2, chart.Bottom + U(18));
                }
            }
        }

        // Now
        if (now >= first && now < periods[^1].End)
        {
            var nx = X(now);
            using var pen = new Pen(P.TextPrimary, Math.Max(1f, U(1.3f)));
            g.DrawLine(pen, nx, chart.Y - U(4), nx, chart.Bottom);
            DrawText(g, L.T("now", "nu"), F.Tiny, P.TextPrimary, nx + U(3), chart.Y - U(6));
        }
        DrawText(g, Unit.Symbol, F.Tiny, P.TextDim, x, chart.Y - U(16));
        return chart.Bottom + U(26);
    }

    // ---- Appliances, rules and history ----

    private float DrawAppliances(Graphics g, IReadOnlyList<PricePeriod> periods, DateTime now, float x, float y, float w)
    {
        var s = Settings;
        if (s.Appliances.Count == 0) return y;
        y = Divider(g, x, y + U(4), w);
        y = Subheading(g, L.T("APPLIANCES – CHEAPEST START", "APPARATER – BILLIGSTE START"), x, y);
        foreach (var a in s.Appliances)
        {
            var run = s.PlannedRuns.FirstOrDefault(r => r.ApplianceId == a.Id);
            var plan = run == null ? AppliancePlanner.Plan(a, periods, now) : null;
            DrawText(g, Fit(g, a.Name, F.Body, w * 0.4f), F.Body, P.TextSecondary, x, y);
            DrawText(g, AppliancePlanner.FormatDuration(a.DurationMinutes) + (a.EnergyKwh > 0 ? $"  ·  {a.EnergyKwh.ToString("0.#", Fmt)} kWh" : ""),
                F.Small, P.TextDim, x, y + U(17));

            // The button on the right: plan or cancel
            var button = run == null ? L.T("Plan", "Planlæg") : run.Started ? "" : L.T("Cancel", "Annullér");
            var bw = 0f;
            if (button.Length > 0 && (run != null || plan != null))
            {
                var size = Measure(g, button, F.SmallBold);
                var rect = new RectangleF(x + w - size.Width - U(16), y + U(4), size.Width + U(16), U(24));
                var over = rect.Contains(Mouse);
                FillRound(g, rect, run == null ? (over ? P.Blue : Color.FromArgb(P.IsDark ? 60 : 40, P.Blue)) : (over ? P.Track : P.HoverBg), U(5));
                DrawText(g, button, F.SmallBold, run == null && over ? Color.White : run == null ? P.Blue : P.TextSecondary, rect.X + U(8), rect.Y + U(4));
                var appliance = a;
                AddHit(rect, run == null
                        ? L.T("Start at the cheapest time – LabWidge reminds you", "Start på det billigste tidspunkt – LabWidge minder dig om det")
                          + (!string.IsNullOrWhiteSpace(a.HomeAssistantEntity) && s.HasHomeAssistantConnection ? L.T($" and switches {a.HomeAssistantEntity} on", $" og tænder {a.HomeAssistantEntity}") : "")
                        : L.T("Cancel the planned run", "Annullér den planlagte kørsel"),
                    () => { AppliancePlanner.Toggle(Settings, appliance, _el.ConsumerPeriods(Settings), DateTime.Now); RequestRedraw(); });
                bw = rect.Width + U(12);
            }

            string line1, line2 = "";
            var color = P.TextPrimary;
            if (run != null)
            {
                var window = new PriceWindow(run.Start, run.End, run.Average);
                line1 = run.Started ? L.T($"Running until {run.End:HH:mm}", $"Kører til kl. {run.End:HH:mm}") : L.T("Planned ", "Planlagt ") + PriceWindows.Describe(window, now);
                color = P.Blue;
                if (AppliancePlanner.CostText(run, a, Unit) is { } runCost) line2 = L.T("about ", "ca. ") + runCost;
            }
            else if (plan != null)
            {
                line1 = plan.StartsNow(now) ? L.T("Start now", "Start nu") : PriceWindows.Describe(plan.Best, now);
                if (plan.StartsNow(now)) color = P.Green;
                var cost = plan.Cost(a, Unit);
                var saving = plan.Saving(a, Unit);
                line2 = $"{P0(plan.Best.Average)} {Unit.Symbol}"
                        + (cost is { } c ? "  ·  " + Money.Format(c, Unit) : "")
                        + (saving is > 0.005 ? L.T($"  ·  saves {Money.Format(saving.Value, Unit)}", $"  ·  sparer {Money.Format(saving.Value, Unit)}") : "");
            }
            else
            {
                line1 = L.T("Waiting for prices", "Venter på priser");
                color = P.TextDim;
            }
            DrawText(g, line1, F.BodyBold, color, x + w - bw, y, right: true);
            if (line2.Length > 0) DrawText(g, line2, F.Small, P.TextSecondary, x + w - bw, y + U(17), right: true);
            y += U(40);
        }
        return y;
    }

    private float DrawRules(Graphics g, IReadOnlyList<PricePeriod> periods, List<double> values, DateTime now, float x, float y, float w)
    {
        var s = Settings;
        var rules = s.PriceAutomations.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.EntityId)).ToList();
        if (rules.Count == 0) return y;
        y = Divider(g, x, y + U(4), w);
        y = Subheading(g, L.T("HOME ASSISTANT RULES", "HOME ASSISTANT-REGLER"), x, y);
        if (!s.HasHomeAssistantConnection)
            return Wrapped(g, L.T("Connect Home Assistant under Settings → Plugins to run the rules.", "Forbind Home Assistant under Indstillinger → Plugins for at køre reglerne."), F.Small, P.Amber, x, y, w);
        foreach (var rule in rules)
        {
            var applies = AutomationRules.Applies(rule, periods, values, now);
            var next = AutomationRules.Upcoming(rule, periods, values, now).FirstOrDefault(u => u.Start > now);
            DrawText(g, Fit(g, rule.Name.Length > 0 ? rule.Name : rule.EntityId, F.Body, w * 0.55f), F.Body, P.TextSecondary, x, y);
            DrawText(g, Fit(g, AutomationRules.Describe(rule, Unit), F.Small, w * 0.55f), F.Small, P.TextDim, x, y + U(17));
            var state = applies == true
                ? (rule.TurnOn ? L.T("On now", "Tændt nu") : L.T("Off now", "Slukket nu"))
                : L.T("Waiting", "Venter");
            DrawText(g, state, F.BodyBold, applies == true ? P.Green : P.TextSecondary, x + w, y, right: true);
            if (next.End > next.Start)
                DrawText(g, L.T("next ", "næste ") + PriceWindows.Describe(next, now), F.Small, P.TextSecondary, x + w, y + U(17), right: true);
            AddHit(new RectangleF(x, y - U(2), w, U(38)), $"{rule.EntityId}\n{AutomationRules.Describe(rule, Unit)}"
                + (rule.Revert ? L.T("\nDoes the opposite afterwards", "\nGør det modsatte bagefter") : ""));
            y += U(40);
        }
        return y;
    }

    private float DrawHistory(Graphics g, DateTime now, float x, float y, float w)
    {
        var s = Settings;
        if (!s.PriceHistoryEnabled || _el.FetchedArea.Length == 0) return y;
        var days = PriceHistory.DaysFor(_el.FetchedArea).Where(d => d.Date > now.Date.AddDays(-30)).ToList();
        if (days.Count < 2) return y;

        y = Divider(g, x, y + U(4), w);
        y = Subheading(g, L.T("THE LAST 30 DAYS", "DE SENESTE 30 DAGE"), x, y);

        // A bar per day with its average price
        var chart = new RectangleF(x, y + U(2), w, U(46));
        var max = Math.Max(1, days.Max(d => d.Average));
        var slot = chart.Width / 30;
        var all = days.Select(d => d.Average).ToList();
        foreach (var d in days)
        {
            var i = 29 - (int)(now.Date - d.Date).TotalDays;
            if (i < 0 || i > 29) continue;
            var h = Math.Max(U(2), (float)(Math.Max(0, d.Average) / max) * chart.Height);
            var rect = new RectangleF(chart.X + i * slot + U(1), chart.Bottom - h, Math.Max(1f, slot - U(2)), h);
            FillRound(g, rect, Color.FromArgb(d.Date == now.Date ? 255 : 190, P.Level(ElectricityPriceService.Level(d.Average, all))), U(1.5f));
            AddHit(new RectangleF(chart.X + i * slot, chart.Y, slot, chart.Height),
                d.Date.ToString(L.T("dddd d MMM", "dddd d. MMM"), Fmt) + L.T($"\naverage {P0(d.Average)} {Unit.PerKwh}", $"\ngennemsnit {P0(d.Average)} {Unit.PerKwh}")
                + L.T($"\nlowest {P0(d.Min)} · highest {P0(d.Max)}", $"\nlaveste {P0(d.Min)} · højeste {P0(d.Max)}"));
        }
        y = chart.Bottom + U(10);

        if (PriceHistory.MonthAverage(_el.FetchedArea, now) is { } month)
            y = KeyValue(g, L.T("Average this month", "Gennemsnit denne måned"), $"{P0(month)} {Unit.Symbol}", x, y, w);
        if (PriceHistory.MonthAverage(_el.FetchedArea, now.AddMonths(-1)) is { } previous)
            y = KeyValue(g, L.T("Average last month", "Gennemsnit sidste måned"), $"{P0(previous)} {Unit.Symbol}", x, y, w);
        var (saved, runs) = PriceHistory.MonthSavings(Unit.Currency, now);
        if (runs > 0)
            y = KeyValue(g, L.T("Saved with planned runs", "Sparet med planlagte kørsler"), Money.Format(saved, Unit), x, y, w, P.Green,
                L.T($"{runs} runs this month, compared with starting each one when it was planned",
                    $"{runs} kørsler denne måned, sammenlignet med at starte hver af dem, da den blev planlagt"));
        return y;
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

    private float PeriodRow(Graphics g, PriceWindow? period, string label, Color color, DateTime now, float x, float y, float w)
    {
        DrawText(g, label, F.Body, P.TextSecondary, x, y);
        if (period is not { } p)
        {
            DrawText(g, "–", F.Body, P.TextDim, x + w, y, right: true);
            return y + U(22);
        }
        var when = PriceWindows.Describe(p, now);
        var starts = p.Start - now;
        var soon = starts > TimeSpan.Zero && starts < TimeSpan.FromHours(12)
            ? L.T($"  ·  in {(int)starts.TotalHours} h {starts.Minutes} min", $"  ·  om {(int)starts.TotalHours} t {starts.Minutes} min")
            : starts <= TimeSpan.Zero ? L.T("  ·  now", "  ·  nu") : "";
        var vw = DrawText(g, $"{P0(p.Average)} {Unit.Symbol}", F.BodyBold, color, x + w, y, right: true);
        DrawText(g, when + soon, F.Body, P.TextPrimary, x + w - vw - U(14), y, right: true);
        return y + U(22);
    }
}
