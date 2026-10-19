using System.Globalization;

internal enum PriceAlertKind { Cheap, Expensive }

internal sealed record PriceAlert(PriceAlertKind Kind, DateTime StartsAt, string Title, string Line1, string Line2);

/// <summary>
/// Decides whether to send a price alert: when the cheapest stretch (3 hours by default) is approaching,
/// or when power is about to enter an expensive period. Works on the source's own periods – quarter-hours
/// where the market has them – so a cheap stretch can start at 13:15 and not only on the hour.
/// </summary>
internal static class PriceAlerts
{
    private static CultureInfo Da => L.Culture;

    /// <summary>"3 hours", "2.5 hours" – the length of the cheap stretch.</summary>
    public static string Hours(int minutes, bool danish)
    {
        var h = minutes / 60.0;
        var text = h.ToString("0.##", Da);
        return danish ? $"{text} {(h == 1 ? "time" : "timer")}" : $"{text} {(h == 1 ? "hour" : "hours")}";
    }

    /// <summary>The same in the user's language.</summary>
    public static string Hours(int minutes) => Hours(minutes, L.T("en", "da") == "da");

    public static IEnumerable<PriceAlert> Evaluate(ElectricityPriceService el, AppSettings s, DateTime now)
    {
        var alerts = new List<PriceAlert>();
        var u = s.PriceUnit.PerKwh;
        string Ore(double v) => s.PriceUnit.Format(v, Da);
        if (!s.AlertCheap && !s.AlertExpensive) return alerts;
        if (s.AlertQuietNight && (now.Hour >= 23 || now.Hour < 7)) return alerts;

        var periods = el.ConsumerPeriods(s);
        if (PriceWindows.At(periods, now) is not { } current) return alerts;
        var values = el.HourlyConsumer(s).Select(h => h.Value).ToList();
        if (values.Count < 3) return alerts;
        var lead = TimeSpan.FromMinutes(Math.Clamp(s.AlertLeadMinutes, 5, 240));

        if (s.AlertCheap)
        {
            var minutes = Math.Clamp(s.AlertCheapMinutes, 30, 480);
            var length = TimeSpan.FromMinutes(minutes);
            if (PriceWindows.Cheapest(periods, now, length, latestEnd: now.AddHours(24) + length) is { } best)
            {
                var until = best.Start - now;
                if (until > TimeSpan.Zero && until <= lead && s.LastCheapAlertFor != best.Start)
                {
                    alerts.Add(new PriceAlert(PriceAlertKind.Cheap, best.Start,
                        L.T($"Cheap power in {Minutes(until)} min", $"Billig strøm om {Minutes(until)} min"),
                        L.T($"{best.Start:HH:mm}–{best.End:HH:mm} ({Hours(minutes, false)}) costs {Ore(best.Average)} {u} on average",
                            $"Kl. {best.Start:HH:mm}–{best.End:HH:mm} ({Hours(minutes, true)}) koster i gennemsnit {Ore(best.Average)} {u}"),
                        L.T($"Right now: {Ore(current.Value)} {u}", $"Lige nu: {Ore(current.Value)} {u}")));
                }
            }
        }

        if (s.AlertExpensive && ElectricityPriceService.Level(current.Value, values) != PriceLevel.Expensive)
        {
            var ahead = periods.Where(p => p.Start >= current.End).ToList();
            for (var i = 0; i < ahead.Count; i++)
            {
                if (ElectricityPriceService.Level(ahead[i].Value, values) != PriceLevel.Expensive) continue;

                var start = ahead[i].Start;
                var until = start - now;
                if (until > lead) break;

                var end = i;
                while (end + 1 < ahead.Count && ahead[end + 1].Start == ahead[end].End
                       && ElectricityPriceService.Level(ahead[end + 1].Value, values) == PriceLevel.Expensive) end++;
                var peak = ahead.Skip(i).Take(end - i + 1).Max(p => p.Value);

                if (s.LastExpensiveAlertFor != start)
                {
                    alerts.Add(new PriceAlert(PriceAlertKind.Expensive, start,
                        L.T($"Power gets more expensive in {Minutes(until)} min", $"Strømmen bliver dyrere om {Minutes(until)} min"),
                        L.T($"From {start:HH:mm} the price rises to {Ore(ahead[i].Value)} {u} (up to {Ore(peak)})", $"Fra kl. {start:HH:mm} stiger prisen til {Ore(ahead[i].Value)} {u} (op til {Ore(peak)})"),
                        L.T($"Expensive until about {ahead[end].End:HH:mm} · now {Ore(current.Value)} {u}", $"Dyr periode til ca. kl. {ahead[end].End:HH:mm} · nu {Ore(current.Value)} {u}")));
                }
                break;
            }
        }
        return alerts;
    }

    private static int Minutes(TimeSpan t) => Math.Max(1, (int)Math.Ceiling(t.TotalMinutes));
}
