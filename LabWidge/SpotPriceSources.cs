using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

/// <summary>One spot price from a source: the start of the period in UTC and the price per kWh in whole currency units, excl. VAT.</summary>
internal readonly record struct SpotSample(DateTime StartUtc, double PerKwh);

/// <summary>
/// Fetches today's and tomorrow's day-ahead prices from the free public source for each price area.
/// Each parser is pure, so the formats are covered by the regression tests.
/// </summary>
internal static class SpotPriceSources
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Prices from the start of <paramref name="today"/> (local date) and as far ahead as published.</summary>
    public static async Task<List<SpotSample>> FetchAsync(HttpClient http, PriceArea area, DateTime today, CancellationToken cancel = default)
    {
        var fromUtc = today.Date.ToUniversalTime();
        var toUtc = today.Date.AddDays(2).ToUniversalTime();
        var list = area.Source switch
        {
            PriceSource.EnergiDataService => ParseEnergiDataService(await http.GetStringAsync(
                "https://api.energidataservice.dk/dataset/DayAheadPrices?start=" + today.ToString("yyyy-MM-dd'T'HH:mm", Inv)
                + "&filter=" + Uri.EscapeDataString($"{{\"PriceArea\":[\"{area.Code}\"]}}") + "&sort=TimeUTC%20asc&limit=1000", cancel)),
            PriceSource.Elprisetjustnu => await PerDayAsync(http, today,
                d => $"https://www.elprisetjustnu.se/api/v1/prices/{d:yyyy}/{d:MM-dd}_{area.Code}.json", json => ParseNordicDaily(json, "SEK_per_kWh"), cancel),
            PriceSource.Hvakosterstrommen => await PerDayAsync(http, today,
                d => $"https://www.hvakosterstrommen.no/api/v1/prices/{d:yyyy}/{d:MM-dd}_{area.Code}.json", json => ParseNordicDaily(json, "NOK_per_kWh"), cancel),
            PriceSource.Elering => ParseElering(await http.GetStringAsync(
                $"https://dashboard.elering.ee/api/nps/price?start={Iso(fromUtc)}&end={Iso(toUtc)}", cancel), area.Code.ToLowerInvariant()),
            PriceSource.Awattar => ParseAwattar(await http.GetStringAsync(
                $"https://api.awattar.{(area.Code == "AT" ? "at" : "de")}/v1/marketdata?start={UnixMs(fromUtc)}&end={UnixMs(toUtc)}", cancel)),
            PriceSource.EnergyZero => ParseEnergyZero(await http.GetStringAsync(
                $"https://api.energyzero.nl/v1/energyprices?fromDate={Iso(fromUtc)}&tillDate={Iso(toUtc)}&interval=4&usageType=1&inclBtw=false", cancel)),
            PriceSource.Pse => ParsePse(await http.GetStringAsync(
                "https://api.raporty.pse.pl/api/rce-pln?$filter=" + Uri.EscapeDataString($"business_date ge '{today:yyyy-MM-dd}'")
                + "&$select=dtime_utc,rce_pln&$first=400", cancel)),
            PriceSource.Omie => await PerDayAsync(http, today,
                d => $"https://www.omie.es/es/file-download?parents=marginalpdbc&filename=marginalpdbc_{d:yyyyMMdd}.1",
                text => ParseOmie(text, area.Code == "PT" ? 4 : 5), cancel),
            _ => throw new NotSupportedException($"No source for {area.Code}")
        };
        list.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        // Keep the requested window; a source may return a little more
        return list.Where(p => p.StartUtc >= fromUtc && p.StartUtc < toUtc).GroupBy(p => p.StartUtc).Select(g => g.First()).ToList();
    }

    /// <summary>Sources with one file per day: today must exist, tomorrow is added when it has been published.</summary>
    private static async Task<List<SpotSample>> PerDayAsync(HttpClient http, DateTime today, Func<DateTime, string> url,
                                                           Func<string, List<SpotSample>> parse, CancellationToken cancel)
    {
        var list = parse(await http.GetStringAsync(url(today.Date), cancel));
        try
        {
            using var response = await http.GetAsync(url(today.Date.AddDays(1)), cancel);
            if (response.IsSuccessStatusCode) list.AddRange(parse(await response.Content.ReadAsStringAsync(cancel)));
            else if (response.StatusCode != HttpStatusCode.NotFound) Logger.Info($"Tomorrow's prices: HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Tomorrow's prices are optional; the next refresh tries again
        }
        return list;
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Inv);
    private static long UnixMs(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();

    // ---------- Parsers ----------

    /// <summary>Energinet: TimeUTC and DayAheadPriceDKK in DKK/MWh.</summary>
    public static List<SpotSample> ParseEnergiDataService(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<SpotSample>();
        foreach (var rec in doc.RootElement.GetProperty("records").EnumerateArray())
        {
            var time = DateTime.SpecifyKind(DateTime.ParseExact(rec.GetProperty("TimeUTC").GetString()!, "yyyy-MM-dd'T'HH:mm:ss", Inv), DateTimeKind.Utc);
            double perMwh;
            if (rec.TryGetProperty("DayAheadPriceDKK", out var dkk) && dkk.ValueKind == JsonValueKind.Number) perMwh = dkk.GetDouble();
            else if (rec.TryGetProperty("DayAheadPriceEUR", out var eur) && eur.ValueKind == JsonValueKind.Number) perMwh = eur.GetDouble() * 7.46;
            else continue;
            list.Add(new SpotSample(time, perMwh / 1000));
        }
        return list;
    }

    /// <summary>elprisetjustnu.se and hvakosterstrommen.no: an array with time_start (with offset) and the price per kWh.</summary>
    public static List<SpotSample> ParseNordicDaily(string json, string priceField)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => new SpotSample(DateTimeOffset.Parse(e.GetProperty("time_start").GetString()!, Inv).UtcDateTime,
                                        e.GetProperty(priceField).GetDouble()))
            .ToList();
    }

    /// <summary>Elering: data.{ee|fi|lv|lt}[] with a Unix timestamp in seconds and EUR/MWh.</summary>
    public static List<SpotSample> ParseElering(string json, string zone)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.GetProperty("data").TryGetProperty(zone, out var rows)) return new List<SpotSample>();
        return rows.EnumerateArray()
            .Select(e => new SpotSample(DateTimeOffset.FromUnixTimeSeconds(e.GetProperty("timestamp").GetInt64()).UtcDateTime,
                                        e.GetProperty("price").GetDouble() / 1000))
            .ToList();
    }

    /// <summary>aWATTar: data[] with start_timestamp in milliseconds and marketprice in EUR/MWh.</summary>
    public static List<SpotSample> ParseAwattar(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => new SpotSample(DateTimeOffset.FromUnixTimeMilliseconds(e.GetProperty("start_timestamp").GetInt64()).UtcDateTime,
                                        e.GetProperty("marketprice").GetDouble() / 1000))
            .ToList();
    }

    /// <summary>EnergyZero: Prices[] with readingDate in UTC and the price in EUR/kWh excl. VAT.</summary>
    public static List<SpotSample> ParseEnergyZero(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("Prices").EnumerateArray()
            .Select(e => new SpotSample(DateTimeOffset.Parse(e.GetProperty("readingDate").GetString()!, Inv, DateTimeStyles.AssumeUniversal).UtcDateTime,
                                        e.GetProperty("price").GetDouble()))
            .ToList();
    }

    /// <summary>PSE: value[] with dtime_utc = the END of a quarter-hour and rce_pln in PLN/MWh.</summary>
    public static List<SpotSample> ParsePse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(e => new SpotSample(
                DateTime.SpecifyKind(DateTime.ParseExact(e.GetProperty("dtime_utc").GetString()!, "yyyy-MM-dd HH:mm:ss", Inv), DateTimeKind.Utc).AddMinutes(-15),
                e.GetProperty("rce_pln").GetDouble() / 1000))
            .ToList();
    }

    private static readonly TimeZoneInfo Madrid = FindZone("Romance Standard Time", "Europe/Madrid");

    /// <summary>
    /// OMIE marginalpdbc: "year;month;day;period;PT;ES;" in EUR/MWh. Periods count from midnight Spanish time –
    /// 96 quarter-hours (92/100 on clock-change days), or 24 hours in older files.
    /// </summary>
    public static List<SpotSample> ParseOmie(string text, int priceColumn)
    {
        var rows = new List<(DateTime Day, int Period, double Price)>();
        foreach (var line in text.Split('\n'))
        {
            var f = line.Trim().Split(';');
            if (f.Length <= priceColumn || !int.TryParse(f[0], out var year) || !int.TryParse(f[3], out var period)) continue;
            if (!double.TryParse(f[priceColumn], NumberStyles.Float, Inv, out var price)) continue;
            rows.Add((new DateTime(year, int.Parse(f[1], Inv), int.Parse(f[2], Inv)), period, price));
        }
        if (rows.Count == 0) return new List<SpotSample>();
        var minutes = rows.Max(r => r.Period) > 25 ? 15 : 60;
        return rows.Select(r => new SpotSample(
                TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(r.Day, DateTimeKind.Unspecified), Madrid).AddMinutes((r.Period - 1) * minutes),
                r.Price / 1000))
            .ToList();
    }

    private static TimeZoneInfo FindZone(params string[] ids)
    {
        foreach (var id in ids)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.Utc;
    }
}
