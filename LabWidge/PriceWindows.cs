/// <summary>A price period in local time with the price as the user sees it (the country's unit per kWh).</summary>
internal readonly record struct PricePeriod(DateTime Start, DateTime End, double Value)
{
    /// <summary>The real length, also the night the clocks change (01:45–03:00 is a quarter-hour).</summary>
    public TimeSpan Length => PriceWindows.Between(Start, End);
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

    /// <summary>
    /// Turns price points (start and value) into periods. A period ends where the next begins, and lasts at most an hour.
    /// Lengths are measured in UTC, so the night the clocks go forward is not a gap: 01:45 is followed by 03:00.
    /// </summary>
    public static List<PricePeriod> FromPoints(IReadOnlyList<(DateTime Start, double Value)> points)
    {
        var periods = new List<PricePeriod>(points.Count);
        var last = MaxPeriod;
        for (var i = 0; i < points.Count; i++)
        {
            var length = i + 1 < points.Count ? Utc(points[i + 1].Start) - Utc(points[i].Start) : last;
            if (length <= TimeSpan.Zero || length > MaxPeriod) length = last; // a gap in the data
            last = length;
            var start = points[i].Start;
            var end = start.Kind == DateTimeKind.Local ? (Utc(start) + length).ToLocalTime() : start + length;
            // In the repeated hour the night the clocks go back, local time runs backwards: the period keeps its length,
            // and the two periods with the same local time overlap for that one hour a year
            if (end <= start) end = start + length;
            periods.Add(new PricePeriod(start, end, points[i].Value));
        }
        return periods;
    }

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t;

    /// <summary>
    /// The time that passes from <paramref name="from"/> to <paramref name="to"/>. Around a clock change the wall clock and UTC
    /// disagree in opposite directions – forward in spring (01:45–03:00 is 15 minutes, not 75), and in the repeated autumn hour,
    /// which local time can't tell apart – and the shorter of the two is the real length.
    /// </summary>
    public static TimeSpan Between(DateTime from, DateTime to)
    {
        var wall = to - from;
        var real = Utc(to) - Utc(from);
        return wall > TimeSpan.Zero && real > TimeSpan.Zero ? (wall < real ? wall : real) : wall;
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
            var minutes = Between(from, to).TotalMinutes;
            sum += p.Value * minutes;
            covered += minutes;
        }
        return covered >= Between(start, end).TotalMinutes - 0.5 ? sum / covered : null;
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
    /// not necessarily side by side. The last one is cut short when only part of it is needed (e.g. 1¼ hours of hourly prices).
    /// Sorted by time.
    /// </summary>
    public static List<PricePeriod> CheapestSet(IReadOnlyList<PricePeriod> periods, DateTime from, DateTime to, TimeSpan total)
    {
        var chosen = new List<PricePeriod>();
        var sum = TimeSpan.Zero;
        foreach (var p in periods.Where(p => p.Start >= from && p.End <= to).OrderBy(p => p.Value).ThenBy(p => p.Start))
        {
            if (sum >= total) break;
            var left = total - sum;
            chosen.Add(p.Length > left ? p with { End = p.Start + left } : p);
            sum += p.Length > left ? left : p.Length;
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
