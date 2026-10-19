using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

/// <summary>One day's prices: the spot price and the price the user saw (with tariffs, add-on and VAT as set at the time).</summary>
internal sealed class PriceHistoryDay
{
    public DateTime Date { get; set; }
    /// <summary>"DK:DK1" – prices from different areas are never mixed.</summary>
    public string Area { get; set; } = "";
    /// <summary>The length of each period: 15 or 60.</summary>
    public int Minutes { get; set; } = 60;
    /// <summary>The first period's start in UTC. The periods follow each other in UTC, so the clock change on a 23- or
    /// 25-hour day is kept. Missing in days saved before it was added.</summary>
    public DateTime? StartUtc { get; set; }

    /// <summary>The local start of period <paramref name="i"/>.</summary>
    public DateTime PeriodStart(int i) => StartUtc is { } utc
        ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddMinutes(i * Minutes).ToLocalTime()
        : Date.AddMinutes(i * Minutes);
    public double[] Spot { get; set; } = Array.Empty<double>();
    public double[] Price { get; set; } = Array.Empty<double>();

    public double Average => Price.Length == 0 ? 0 : Price.Average();
    public double Min => Price.Length == 0 ? 0 : Price.Min();
    public double Max => Price.Length == 0 ? 0 : Price.Max();
}

/// <summary>A run of an appliance that LabWidge planned, and how much it saved compared with starting it right away.</summary>
internal sealed class SavingRecord
{
    public DateTime Start { get; set; }
    public string Name { get; set; } = "";
    public double EnergyKwh { get; set; }
    /// <summary>In whole currency units.</summary>
    public double Cost { get; set; }
    public double Saved { get; set; }
    public string Currency { get; set; } = "";
}

internal sealed class PriceHistoryData
{
    public int Version { get; set; } = 1;
    public List<PriceHistoryDay> Days { get; set; } = new();
    public List<SavingRecord> Savings { get; set; } = new();
}

/// <summary>
/// The prices of past days and the savings of planned runs, kept in price-history.json next to the settings.
/// Nothing leaves the PC.
/// </summary>
internal static class PriceHistory
{
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabWidge", "price-history.json");

    private static PriceHistoryData? _data;
    public static PriceHistoryData Data => _data ??= Load();

    public static event Action? Changed;

    /// <summary>Forgets what is loaded, e.g. when a test points <see cref="FilePath"/> somewhere else.</summary>
    public static void Reset() => _data = null;

    private static PriceHistoryData Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<PriceHistoryData>(File.ReadAllText(FilePath)) ?? new PriceHistoryData();
        }
        catch (Exception ex)
        {
            Logger.Error($"The price history could not be read and starts again: {ex.Message}");
        }
        return new PriceHistoryData();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Data));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Error($"The price history could not be saved: {ex.Message}");
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Keeps every whole day up to <paramref name="today"/> from <paramref name="prices"/>. True if something was added or changed.
    /// </summary>
    public static bool Record(IReadOnlyList<PricePoint> prices, Func<DateTime, double, double> consumer, string area, int keepDays, DateTime today)
    {
        if (area.Length == 0 || prices.Count == 0) return false;
        var changed = false;
        foreach (var day in prices.GroupBy(p => p.Time.Date).Where(g => g.Key <= today.Date))
        {
            if (ToDay(day.Key, day.OrderBy(p => p.Time).ToList(), consumer, area) is not { } entry) continue;
            var existing = Data.Days.FindIndex(d => d.Date == entry.Date && d.Area == area);
            if (existing >= 0)
            {
                var old = Data.Days[existing];
                // Periods that have passed keep the price the user saw then, even if VAT or tariffs changed since
                if (old.Minutes == entry.Minutes && old.StartUtc == entry.StartUtc && old.Price.Length == entry.Price.Length)
                    for (var i = 0; i < entry.Price.Length; i++)
                        if (entry.PeriodStart(i).AddMinutes(entry.Minutes) <= DateTime.Now) entry.Price[i] = old.Price[i];
                if (old.Minutes == entry.Minutes && old.StartUtc == entry.StartUtc && old.Spot.SequenceEqual(entry.Spot) && old.Price.SequenceEqual(entry.Price)) continue;
                Data.Days[existing] = entry;
            }
            else Data.Days.Add(entry);
            changed = true;
        }
        var oldest = today.Date.AddDays(-Math.Clamp(keepDays, 7, 2000));
        changed |= Data.Days.RemoveAll(d => d.Date < oldest) > 0;
        changed |= Data.Savings.RemoveAll(s => s.Start < oldest) > 0;
        if (changed)
        {
            Data.Days.Sort((a, b) => a.Date.CompareTo(b.Date));
            Save();
        }
        return changed;
    }

    /// <summary>A whole day of prices, or null if the day is incomplete.</summary>
    private static PriceHistoryDay? ToDay(DateTime date, List<PricePoint> points, Func<DateTime, double, double> consumer, string area)
    {
        if (points.Count < 2) return null;
        var minutes = (int)Math.Round((points[1].Time - points[0].Time).TotalMinutes);
        if (minutes is not (15 or 30 or 60)) return null;
        // Whole means every period from midnight to the next, measured in real time: 23 or 25 hours the days the clocks change
        if (points[0].Time != date) return null;
        var length = (date.AddDays(1).ToUniversalTime() - date.ToUniversalTime()).TotalMinutes;
        if (points.Count * minutes != (int)Math.Round(length)) return null;
        for (var i = 1; i < points.Count; i++)
            if ((int)Math.Round((points[i].Time.ToUniversalTime() - points[i - 1].Time.ToUniversalTime()).TotalMinutes) != minutes) return null;
        return new PriceHistoryDay
        {
            Date = date, Area = area, Minutes = minutes, StartUtc = points[0].Time.ToUniversalTime(),
            Spot = points.Select(p => Math.Round(p.Spot, 3)).ToArray(),
            Price = points.Select(p => Math.Round(consumer(p.Time, p.Spot), 3)).ToArray()
        };
    }

    public static void AddSaving(SavingRecord record)
    {
        Data.Savings.Add(record);
        Save();
    }

    public static void Clear()
    {
        _data = new PriceHistoryData();
        Save();
    }

    public static IReadOnlyList<PriceHistoryDay> DaysFor(string area) => Data.Days.Where(d => d.Area == area).ToList();

    /// <summary>The average price over the days of a month that are kept, or null if none are.</summary>
    public static double? MonthAverage(string area, DateTime month)
    {
        var days = Data.Days.Where(d => d.Area == area && d.Date.Year == month.Year && d.Date.Month == month.Month).ToList();
        return days.Count == 0 ? null : days.Average(d => d.Average);
    }

    /// <summary>Savings in a month, in the given currency.</summary>
    public static (double Saved, int Runs) MonthSavings(string currency, DateTime month)
    {
        var runs = Data.Savings.Where(s => s.Currency == currency && s.Start.Year == month.Year && s.Start.Month == month.Month).ToList();
        return (runs.Sum(s => s.Saved), runs.Count);
    }

    public static string ToCsv(string area)
    {
        var inv = CultureInfo.InvariantCulture;
        var csv = new StringBuilder("start,spot,price\n");
        foreach (var day in DaysFor(area))
        {
            for (var i = 0; i < day.Price.Length; i++)
            {
                var start = day.PeriodStart(i);
                csv.Append(start.ToString("yyyy-MM-dd HH:mm", inv)).Append(',')
                   .Append(day.Spot.ElementAtOrDefault(i).ToString("0.###", inv)).Append(',')
                   .Append(day.Price[i].ToString("0.###", inv)).Append('\n');
            }
        }
        return csv.ToString();
    }

    /// <summary>
    /// Fetches the spot prices of the last <paramref name="days"/> days from the country's source, for days not kept yet.
    /// The price uses today's settings and the tariffs that applied on each day.
    /// </summary>
    public static async Task<int> BackfillAsync(HttpClient http, ElectricityPriceService el, AppSettings s, int days,
                                                IProgress<string>? progress, CancellationToken cancel)
    {
        if (s.PriceCountry is not { } country) return 0;
        var area = country.AreaOrDefault(s.PriceArea);
        var key = $"{country.Code}:{area.Code}";
        var added = 0;
        var day = DateTime.Today.AddDays(-Math.Clamp(days, 1, 365));
        while (day < DateTime.Today)
        {
            cancel.ThrowIfCancellationRequested();
            if (Data.Days.Any(d => d.Area == key && d.Date == day))
            {
                day = day.AddDays(1);
                continue;
            }
            progress?.Report(L.T($"Fetching {day:d MMM} …", $"Henter {day:d. MMM} …"));
            var samples = await SpotPriceSources.FetchAsync(http, area, country.Unit.Currency, day, cancel);
            var points = samples.Select(p => new PricePoint(p.StartUtc.ToLocalTime(), p.PerKwh * country.Unit.PerMajor))
                                .Where(p => p.Time.Date < DateTime.Today).ToList();
            var before = Data.Days.Count;
            Record(points, (t, spot) => el.Consumer(t, spot, s), key, s.PriceHistoryDays, DateTime.Today.AddDays(-1));
            added += Data.Days.Count - before;
            // A source can return several days at once – continue after the last one it gave
            var last = points.Count > 0 ? points.Max(p => p.Time).Date : day;
            day = (last >= day ? last : day).AddDays(1);
            await Task.Delay(300, cancel);
        }
        return added;
    }
}
