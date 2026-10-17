/// <summary>A price period in local time with the price as the user sees it (the country's unit per kWh).</summary>
internal readonly record struct PricePeriod(DateTime Start, DateTime End, double Value)
{
    public TimeSpan Length => End - Start;
}

/// <summary>A stretch of time and its time-weighted average price.</summary>
internal readonly record struct PriceWindow(DateTime Start, DateTime End, double Average)
{
    public TimeSpan Length => End - Start;
}

/// <summary>
/// Finds cheap (or expensive) stretches in the price periods. Works the same for quarter-hours and whole hours,
/// and for lengths that are not a whole number of periods (e.g. a dishwasher running 2 h 15 min on hourly prices).
/// </summary>
internal static class PriceWindows
{
    private static readonly TimeSpan MaxPeriod = TimeSpan.FromHours(1);

    /// <summary>Turns price points (start and value) into periods. A period ends where the next begins, and lasts at most an hour.</summary>
    public static List<PricePeriod> FromPoints(IReadOnlyList<(DateTime Start, double Value)> points)
    {
        var periods = new List<PricePeriod>(points.Count);
        var last = MaxPeriod;
        for (var i = 0; i < points.Count; i++)
        {
            var length = i + 1 < points.Count ? points[i + 1].Start - points[i].Start : last;
            if (length <= TimeSpan.Zero || length > MaxPeriod) length = last; // a gap in the data
            last = length;
            periods.Add(new PricePeriod(points[i].Start, points[i].Start + length, points[i].Value));
        }
        return periods;
    }

    /// <summary>The period that contains <paramref name="t"/>, if any.</summary>
    public static PricePeriod? At(IReadOnlyList<PricePeriod> periods, DateTime t)
    {
        foreach (var p in periods)
        {
            if (p.Start > t) break;
            if (t < p.End) return p;
        }
        return null;
    }

    /// <summary>The time-weighted average from <paramref name="start"/> to <paramref name="end"/>; null if the prices don't cover it all.</summary>
    public static double? Average(IReadOnlyList<PricePeriod> periods, DateTime start, DateTime end)
    {
        if (end <= start) return null;
        double sum = 0, covered = 0;
        foreach (var p in periods)
        {
            if (p.End <= start) continue;
            if (p.Start >= end) break;
            var from = p.Start > start ? p.Start : start;
            var to = p.End < end ? p.End : end;
            var minutes = (to - from).TotalMinutes;
            sum += p.Value * minutes;
            covered += minutes;
        }
        return covered >= (end - start).TotalMinutes - 0.5 ? sum / covered : null;
    }

    /// <summary>
    /// The cheapest (or most expensive) stretch of <paramref name="length"/>. It starts at a period boundary from the current
    /// period on – or, with <paramref name="startNow"/>, right now or at a later boundary – and ends by <paramref name="latestEnd"/>.
    /// </summary>
    public static PriceWindow? Cheapest(IReadOnlyList<PricePeriod> periods, DateTime from, TimeSpan length,
                                        DateTime? latestEnd = null, bool startNow = false, bool cheapest = true)
    {
        if (length <= TimeSpan.Zero || periods.Count == 0) return null;
        var starts = new List<DateTime>();
        if (startNow) starts.Add(from);
        foreach (var p in periods)
        {
            if (startNow ? p.Start > from : p.End > from) starts.Add(p.Start);
        }

        PriceWindow? best = null;
        foreach (var start in starts)
        {
            var end = start + length;
            if (latestEnd is { } limit && end > limit) break;
            if (Average(periods, start, end) is not { } avg) continue;
            if (best == null || (cheapest ? avg < best.Value.Average - 1e-9 : avg > best.Value.Average + 1e-9))
                best = new PriceWindow(start, end, avg);
        }
        return best;
    }

    /// <summary>
    /// The cheapest periods between <paramref name="from"/> and <paramref name="to"/> that together last <paramref name="total"/> –
    /// not necessarily side by side. Sorted by time.
    /// </summary>
    public static List<PricePeriod> CheapestSet(IReadOnlyList<PricePeriod> periods, DateTime from, DateTime to, TimeSpan total)
    {
        var chosen = new List<PricePeriod>();
        var sum = TimeSpan.Zero;
        foreach (var p in periods.Where(p => p.Start >= from && p.End <= to).OrderBy(p => p.Value).ThenBy(p => p.Start))
        {
            if (sum >= total) break;
            chosen.Add(p);
            sum += p.Length;
        }
        chosen.Sort((a, b) => a.Start.CompareTo(b.Start));
        return chosen;
    }

    /// <summary>Joins periods that follow each other into stretches with their average price.</summary>
    public static List<PriceWindow> Merge(IEnumerable<PricePeriod> periods)
    {
        var windows = new List<PriceWindow>();
        DateTime? start = null, end = null;
        double sum = 0, minutes = 0;
        foreach (var p in periods.OrderBy(p => p.Start))
        {
            if (end != p.Start && start != null)
            {
                windows.Add(new PriceWindow(start.Value, end!.Value, sum / minutes));
                start = null;
            }
            if (start == null) { start = p.Start; sum = 0; minutes = 0; }
            end = p.End;
            sum += p.Value * p.Length.TotalMinutes;
            minutes += p.Length.TotalMinutes;
        }
        if (start != null) windows.Add(new PriceWindow(start.Value, end!.Value, sum / minutes));
        return windows;
    }

    /// <summary>"13:15–16:15", with "today"/"tomorrow" when <paramref name="now"/> is given.</summary>
    public static string Describe(PriceWindow w, DateTime? now = null)
    {
        var day = now is not { } n ? "" : w.Start.Date == n.Date ? L.T("today ", "i dag kl. ")
            : w.Start.Date == n.Date.AddDays(1) ? L.T("tomorrow ", "i morgen kl. ")
            : w.Start.ToString(L.T("ddd ", "ddd 'kl.' "), L.Culture);
        return $"{day}{w.Start:HH:mm}–{w.End:HH:mm}";
    }
}
