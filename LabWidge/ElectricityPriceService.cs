using System.Globalization;
using System.Net.Http;
using System.Text.Json;

/// <summary>A spot price period in local time. Spot is in the country's price unit per kWh (e.g. øre or ct) excluding VAT.</summary>
internal sealed record PricePoint(DateTime Time, double Spot);

/// <summary>One tariff/tax line from Datahub. Prices are øre/kWh excluding VAT, per hour (index 0-23).</summary>
internal sealed record TariffRow(string Code, string Note, DateTime ValidFrom, DateTime? ValidTo, double[] HourlyOre)
{
    public bool IsValidAt(DateTime t) => ValidFrom <= t && (ValidTo == null || t < ValidTo);
}

internal enum PriceLevel { Cheap, Medium, Expensive }

/// <summary>
/// Fetches spot prices for the chosen country (see <see cref="SpotPriceSources"/>) and, in Denmark,
/// tariffs (DatahubPricelist) from Energi Data Service.
/// </summary>
internal sealed class ElectricityPriceService
{
    private const string Api = "https://api.energidataservice.dk/dataset/";
    private const string EnerginetGln = "5790000432752";
    private static readonly string[] EnerginetCodes = { "40000", "41000", "EA-001" }; // Transmission, system tariff, electricity tax

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private sealed record FetchRequest(Country? Country, string Area, string Owner, string[] Codes, bool HasNetTariff);
    private FetchRequest? _pendingRefresh;

    public IReadOnlyList<PricePoint> Prices { get; private set; } = Array.Empty<PricePoint>();
    public IReadOnlyList<TariffRow> NetTariffs { get; private set; } = Array.Empty<TariffRow>();
    public IReadOnlyList<TariffRow> StateCharges { get; private set; } = Array.Empty<TariffRow>();
    public DateTime LastFetch { get; private set; } = DateTime.MinValue;
    public DateTime LastSuccessfulFetch { get; private set; } = DateTime.MinValue;
    public DateTime FetchDay { get; private set; } = DateTime.MinValue;
    public string? LastError { get; private set; }
    public string? TariffError { get; private set; }
    public string FetchedArea { get; private set; } = "";

    public event Action? Updated;

    public ElectricityPriceService(HttpClient http) => _http = http;

    public bool HasTomorrow => Prices.Count > 0 && Prices[^1].Time.Date > DateTime.Today;

    /// <summary>Decides whether data should be fetched again (new day, tomorrow's prices published, an error or old data).</summary>
    public bool NeedsRefresh(AppSettings settings)
    {
        var area = AreaKey(settings);
        var now = DateTime.Now;
        var ageMin = (now - LastFetch).TotalMinutes;
        return area != FetchedArea
            || (TariffError != null && ageMin > 2)
            || FetchDay != now.Date
            || ageMin > 60
            || (LastError != null && ageMin > 2)
            || (!HasTomorrow && now.Hour >= 13 && ageMin > 10);
    }

    public async Task RefreshAsync(AppSettings settings, CancellationToken cancel = default)
    {
        // Remember the latest change instead of losing it during a fetch in progress.
        // Take a copy before the first await, so area and tariff always belong to the same request.
        _pendingRefresh = new FetchRequest(Countries.Find(settings.Country), AreaKey(settings), settings.NetTariffOwner,
            settings.NetTariffCodes.ToArray(), settings.HasNetTariff);
        if (!await _lock.WaitAsync(0)) return;
        try
        {
            while (_pendingRefresh is { } request)
            {
                cancel.ThrowIfCancellationRequested();
                _pendingRefresh = null;
                await RefreshOneAsync(request, cancel);
                Updated?.Invoke();
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { _pendingRefresh = null; throw; }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>"DK:DK1" – the country and area the prices belong to. Empty when the country has no prices.</summary>
    private static string AreaKey(AppSettings s) =>
        Countries.Find(s.Country) is { } c ? $"{c.Code}:{c.AreaOrDefault(s.PriceArea).Code}" : "";

    private async Task RefreshOneAsync(FetchRequest request, CancellationToken cancel)
    {
        var area = request.Area;
        if (request.Country is not { } country)
        {
            Prices = Array.Empty<PricePoint>();
            NetTariffs = StateCharges = Array.Empty<TariffRow>();
            FetchedArea = area;
            FetchDay = DateTime.Today;
            LastError = TariffError = null;
            LastFetch = DateTime.Now;
            return;
        }
        try
        {
            var samples = await SpotPriceSources.FetchAsync(_http, country.AreaOrDefault(area.Split(':').Last()), DateTime.Today, cancel);
            if (samples.Count == 0) throw new InvalidOperationException("The price source returned no prices.");
            Prices = samples.Select(p => new PricePoint(p.StartUtc.ToLocalTime(), p.PerKwh * country.Unit.PerMajor)).ToList();
            FetchedArea = area;
            FetchDay = DateTime.Today;
            LastError = null;
            LastSuccessfulFetch = DateTime.Now;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Logger.Error($"Fetching electricity prices failed: {ex.Message}");
        }

        if (country.Code != "DK")
        {
            // Grid tariffs and taxes from Datahub exist only in Denmark
            NetTariffs = StateCharges = Array.Empty<TariffRow>();
            TariffError = null;
            LastFetch = DateTime.Now;
            return;
        }
        try
        {
            NetTariffs = !request.HasNetTariff
                ? Array.Empty<TariffRow>()
                : await FetchTariffsAsync($"\"ChargeOwner\":[\"{request.Owner}\"]", request.Codes, 400, cancel);
            StateCharges = await FetchTariffsAsync($"\"GLN_Number\":[\"{EnerginetGln}\"]", EnerginetCodes, 30, cancel);
            TariffError = null;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            TariffError = ex.Message;
            Logger.Error($"Fetching tariffs failed: {ex.Message}");
        }

        LastFetch = DateTime.Now;
    }

    private async Task<IReadOnlyList<TariffRow>> FetchTariffsAsync(string ownerFilter, string[] codes, int limit, CancellationToken cancel)
    {
        var codeJson = string.Join(",", codes.Select(c => $"\"{c}\""));
        var filter = Uri.EscapeDataString($"{{{ownerFilter},\"ChargeTypeCode\":[{codeJson}],\"ChargeType\":[\"D03\"]}}");
        var url = $"{Api}DatahubPricelist?filter={filter}&sort=ValidFrom%20desc&limit={limit}";

        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, cancel));
        return ParseTariffRows(doc.RootElement.GetProperty("records"));
    }

    public static List<TariffRow> ParseTariffRows(JsonElement records)
    {
        var rows = new List<TariffRow>();
        foreach (var rec in records.EnumerateArray())
        {
            var hourly = new double[24];
            var p1 = ReadPrice(rec, "Price1") ?? 0;
            for (var h = 0; h < 24; h++)
            {
                hourly[h] = (ReadPrice(rec, $"Price{h + 1}") ?? p1) * 100.0; // DKK/kWh -> øre/kWh
            }
            rows.Add(new TariffRow(
                rec.GetProperty("ChargeTypeCode").GetString() ?? "",
                rec.TryGetProperty("Note", out var note) ? note.GetString() ?? "" : "",
                ParseDate(rec.GetProperty("ValidFrom"))!.Value,
                ParseDate(rec.GetProperty("ValidTo")),
                hourly));
        }
        return rows;
    }

    private static double? ReadPrice(JsonElement rec, string name) =>
        rec.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DateTime? ParseDate(JsonElement e) =>
        e.ValueKind == JsonValueKind.String
            ? DateTime.ParseExact(e.GetString()!, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            : null;

    // ---------- Calculation ----------

    /// <summary>The sum of all valid rows for each code at the time (øre excluding VAT).</summary>
    private static double SumCharges(IReadOnlyList<TariffRow> rows, DateTime t)
    {
        double sum = 0;
        foreach (var group in rows.GroupBy(r => r.Code))
        {
            // The newest valid row for the code wins
            var row = group.Where(r => r.IsValidAt(t)).OrderByDescending(r => r.ValidFrom).FirstOrDefault();
            if (row != null) sum += row.HourlyOre[t.Hour];
        }
        return sum;
    }

    public PriceBreakdown Breakdown(DateTime t, double spot, AppSettings s)
    {
        var total = s.PriceShowTotal;
        var net = total ? SumCharges(NetTariffs, t) : 0;
        var state = total ? SumCharges(StateCharges, t) : 0;
        var supplier = total ? s.SupplierAddOnOre : 0;
        var vat = s.PriceInclVat ? 1 + s.EffectiveVatPercent / 100 : 1.0;
        return new PriceBreakdown(spot * vat, net * vat, state * vat, supplier * vat);
    }

    public double Consumer(DateTime t, double spot, AppSettings s) => Breakdown(t, spot, s).Total;

    public PricePoint? Current(DateTime now)
    {
        PricePoint? cur = null;
        foreach (var p in Prices)
        {
            if (p.Time > now) break;
            cur = p;
        }
        return cur != null && (now - cur.Time).TotalMinutes <= 60 ? cur : null;
    }

    public PricePoint? Next(DateTime now) => Prices.FirstOrDefault(p => p.Time > now);

    /// <summary>Hourly average of the consumer price (for the chart).</summary>
    public IReadOnlyList<(DateTime Hour, double Value)> HourlyConsumer(AppSettings s) =>
        Prices.GroupBy(p => p.Time.Date.AddHours(p.Time.Hour))
              .Select(g => (g.Key, g.Average(p => Consumer(p.Time, p.Spot, s))))
              .OrderBy(x => x.Key)
              .ToList();

    /// <summary>
    /// Level based on where the price sits among the hourly prices (percentile): the cheapest third = cheap etc.
    /// Robust against single price spikes, which would otherwise make almost everything "cheap".
    /// </summary>
    public static PriceLevel Level(double v, IReadOnlyList<double> values)
    {
        if (values.Count == 0) return PriceLevel.Medium;
        var below = values.Count(x => x < v - 0.01);
        var equal = values.Count(x => Math.Abs(x - v) <= 0.01);
        var percentile = (below + equal / 2.0) / values.Count;
        return percentile < 1.0 / 3 ? PriceLevel.Cheap : percentile < 2.0 / 3 ? PriceLevel.Medium : PriceLevel.Expensive;
    }
}

internal readonly record struct PriceBreakdown(double Spot, double NetTariff, double StateCharges, double Supplier)
{
    public double Total => Spot + NetTariff + StateCharges + Supplier;
}
