using System.Drawing;
using System.Drawing.Drawing2D;

// Plugin rendering uses the shared widget drawing context.
internal sealed partial class DashboardForm
{
    internal float DrawPrice(Graphics g, float x, float y, float w)
    {
        var s = _settings;
        var now = DateTime.Now;
        var cur = _el.Current(now);
        var country = s.PriceCountry;
        var area = country?.AreaOrDefault(s.PriceArea);
        var title = L.T("ELECTRICITY", "ELPRIS") + (area == null ? "" : "  ·  " + area.Code);

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
}
