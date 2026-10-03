using System.Globalization;

internal enum PriceAlertKind { Cheap, Expensive }

internal sealed record PriceAlert(PriceAlertKind Kind, DateTime StartsAt, string Title, string Line1, string Line2);

/// <summary>
/// Decides whether to send a price alert: when the cheapest 3 hours are approaching,
/// or when power is about to enter an expensive period.
/// </summary>
internal static class PriceAlerts
{
    private static CultureInfo Da => L.Culture;

    public static IEnumerable<PriceAlert> Evaluate(ElectricityPriceService el, AppSettings s, DateTime now)
    {
        var alerts = new List<PriceAlert>();
        var u = s.PriceUnit.PerKwh;
        string Ore(double v) => s.PriceUnit.Format(v, Da);
        if (!s.AlertCheap && !s.AlertExpensive) return alerts;
        if (s.AlertQuietNight && (now.Hour >= 23 || now.Hour < 7)) return alerts;

        var cur = el.Current(now);
        if (cur == null) return alerts;

        var hours = el.HourlyConsumer(s);
        if (hours.Count < 3) return alerts;
        var values = hours.Select(h => h.Value).ToList();
        var lead = TimeSpan.FromMinutes(Math.Clamp(s.AlertLeadMinutes, 5, 240));
        var curHour = now.Date.AddHours(now.Hour);
        var nowPrice = el.Consumer(cur.Time, cur.Spot, s);

        if (s.AlertCheap)
        {
            var window = hours.Where(h => h.Hour >= curHour && h.Hour < now.AddHours(24)).ToList();
            if (window.Count >= 3)
            {
                var best = 0;
                var bestAvg = double.MaxValue;
                for (var i = 0; i <= window.Count - 3; i++)
                {
                    var avg = (window[i].Value + window[i + 1].Value + window[i + 2].Value) / 3;
                    if (avg < bestAvg) { bestAvg = avg; best = i; }
                }
                var start = window[best].Hour;
                var until = start - now;
                if (until > TimeSpan.Zero && until <= lead && s.LastCheapAlertFor != start)
                {
                    alerts.Add(new PriceAlert(PriceAlertKind.Cheap, start,
                        L.T($"Cheap power in {Minutes(until)} min", $"Billig strøm om {Minutes(until)} min"),
                        L.T($"{start:HH}–{start.AddHours(3):HH} costs {Ore(bestAvg)} {u} on average", $"Kl. {start:HH}–{start.AddHours(3):HH} koster i gennemsnit {Ore(bestAvg)} {u}"),
                        L.T($"Right now: {Ore(nowPrice)} {u}", $"Lige nu: {Ore(nowPrice)} {u}")));
                }
            }
        }

        if (s.AlertExpensive)
        {
            var curIndex = hours.ToList().FindIndex(h => h.Hour == curHour);
            var currentlyExpensive = curIndex >= 0 && ElectricityPriceService.Level(hours[curIndex].Value, values) == PriceLevel.Expensive;
            if (curIndex >= 0 && !currentlyExpensive)
            {
                for (var i = curIndex + 1; i < hours.Count; i++)
                {
                    if (ElectricityPriceService.Level(hours[i].Value, values) != PriceLevel.Expensive) continue;

                    var start = hours[i].Hour;
                    var until = start - now;
                    if (until > lead) break;

                    var end = i;
                    while (end + 1 < hours.Count && ElectricityPriceService.Level(hours[end + 1].Value, values) == PriceLevel.Expensive) end++;
                    var peak = hours.Skip(i).Take(end - i + 1).Max(h => h.Value);

                    if (s.LastExpensiveAlertFor != start)
                    {
                        alerts.Add(new PriceAlert(PriceAlertKind.Expensive, start,
                            L.T($"Power gets more expensive in {Minutes(until)} min", $"Strømmen bliver dyrere om {Minutes(until)} min"),
                            L.T($"From {start:HH}:00 the price rises to {Ore(hours[i].Value)} {u} (up to {Ore(peak)})", $"Fra kl. {start:HH} stiger prisen til {Ore(hours[i].Value)} {u} (op til {Ore(peak)})"),
                            L.T($"Expensive until about {hours[end].Hour.AddHours(1):HH}:00 · now {Ore(nowPrice)} {u}", $"Dyr periode til ca. kl. {hours[end].Hour.AddHours(1):HH} · nu {Ore(nowPrice)} {u}")));
                    }
                    break;
                }
            }
        }
        return alerts;
    }

    private static int Minutes(TimeSpan t) => Math.Max(1, (int)Math.Ceiling(t.TotalMinutes));
}
