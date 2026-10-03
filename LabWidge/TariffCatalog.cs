using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>A candidate for the household tariff (C customer) at a grid company.</summary>
internal sealed record TariffCandidate(string Code, string Note, double NightOre, double DayOre, double PeakOre)
{
    public bool TimeDifferentiated => Math.Abs(PeakOre - NightOre) > 0.01;
    public override string ToString() => $"{Note} ({Code})";
}

internal sealed record TariffDetection(IReadOnlyList<TariffCandidate> Candidates, IReadOnlyList<TariffCandidate> Discounts);

/// <summary>
/// Finds grid companies and their C tariff in Energinet's DatahubPricelist, so new users don't need to know tariff codes.
/// </summary>
internal static class TariffCatalog
{
    private const string Api = "https://api.energidataservice.dk/dataset/DatahubPricelist";

    private static readonly Regex IsCTariff = new(@"nettarif\b.*\bC\b|\bC-kunde\b|\bC-tarif\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Excluded = new(
        @"indfødning|produktion|lokal|kollektiv|samplaceret|egenproducent|rådighed|overordnet|>\s*100|særtarif|flex|skabelon|aftagepligt",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Grid companies that published tariffs in the last six months.</summary>
    public static async Task<IReadOnlyList<string>> GetGridCompaniesAsync(HttpClient http)
    {
        var start = DateTime.Today.AddDays(-180).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var filter = Uri.EscapeDataString("{\"ChargeType\":[\"D03\"]}");
        var url = $"{Api}?filter={filter}&start={start}&columns=ChargeOwner,Note&limit=0";

        using var doc = JsonDocument.Parse(await GetStringWithRetryAsync(http, url));
        var da = StringComparer.Create(CultureInfo.GetCultureInfo("da-DK"), ignoreCase: true);
        return doc.RootElement.GetProperty("records").EnumerateArray()
            .Where(r => (r.GetProperty("Note").GetString() ?? "").Contains("nettarif", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.GetProperty("ChargeOwner").GetString() ?? "")
            .Where(n => n.Length > 0 && !n.StartsWith("Energinet", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, da)
            .ToList();
    }

    public static async Task<TariffDetection> DetectAsync(HttpClient http, string owner)
    {
        var filter = Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string, string[]>
        {
            ["ChargeOwner"] = new[] { owner },
            ["ChargeType"] = new[] { "D03" }
        }));
        var url = $"{Api}?filter={filter}&sort=ValidFrom%20desc&limit=5000";

        using var doc = JsonDocument.Parse(await GetStringWithRetryAsync(http, url));
        var rows = ElectricityPriceService.ParseTariffRows(doc.RootElement.GetProperty("records"));
        var now = DateTime.Now;

        var current = rows
            .Where(r => r.IsValidAt(now))
            .GroupBy(r => r.Code)
            .Select(g => g.OrderByDescending(r => r.ValidFrom).First())
            .Where(r => IsCTariff.IsMatch(r.Note) && !Excluded.IsMatch($"{r.Note} {r.Code}"))
            .Select(r => new TariffCandidate(r.Code, r.Note.Trim(), r.HourlyOre[3], r.HourlyOre[12], r.HourlyOre[18]))
            .ToList();

        var discounts = current.Where(c => c.Note.Contains("rabat", StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = current.Except(discounts)
            .OrderByDescending(c => c.PeakOre > 0.01 || c.DayOre > 0.01)
            .ThenByDescending(c => c.TimeDifferentiated)
            .ThenByDescending(c => c.Note.Contains("time", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new TariffDetection(candidates, discounts);
    }

    /// <summary>Energi Data Service limits the number of calls; wait and retry on 429.</summary>
    private static async Task<string> GetStringWithRetryAsync(HttpClient http, string url)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await http.GetAsync(url);
            if ((int)response.StatusCode == 429 && attempt < 4)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                await Task.Delay(wait > TimeSpan.FromSeconds(20) ? TimeSpan.FromSeconds(20) : wait);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
    }

    /// <summary>Postal codes 1000-4999 are east of the Great Belt (DK2), the rest in DK1.</summary>
    public static string? AreaFromPostalCode(string? postalCode) =>
        int.TryParse(postalCode?.Trim(), out var pc) && pc is >= 1000 and <= 9999
            ? (pc <= 4999 ? "DK2" : "DK1")
            : null;
}
