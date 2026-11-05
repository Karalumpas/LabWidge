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

/// <summary>The grid company the address lookup found: its name, grid area number (e.g. "740") and website.</summary>
internal sealed record GridCompanyFound(string Name, string Area, string? Website);

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

    /// <summary>
    /// Energi Data Service limits the number of calls and answers 429 when they come close together – e.g. right after
    /// the setup guide has looked up grid companies and tariffs. Waits as asked (at most 20 s) and tries up to four times.
    /// </summary>
    internal static async Task<string> GetStringWithRetryAsync(HttpClient http, string url, CancellationToken cancel = default,
                                                             Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (var attempt = 1; ; attempt++)
        {
            using var response = await http.GetAsync(url, cancel);
            if ((int)response.StatusCode == 429 && attempt < 4)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                await delay(wait > TimeSpan.FromSeconds(20) ? TimeSpan.FromSeconds(20) : wait, cancel);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancel);
        }
    }

    // ---------- Grid company from an address ----------

    /// <summary>Green Power Denmark's "Find netselskab" (elnet.dk): which grid company supplies an address. Free and without a key.</summary>
    private const string LookupApi = "https://api.elnet.greenpowerdenmark.dk/api/supplierlookup/";

    /// <summary>The page where users can look the address up themselves.</summary>
    public const string LookupPage = "https://elnet.dk/find-netselskab";

    /// <summary>
    /// Builds "street number postcode" from what the user typed. Floor and door ("1. th") would make the lookup
    /// find nothing, and without a postcode it answers for the first street of that name anywhere in Denmark, so the
    /// postcode comes from the text or else <paramref name="postalFallback"/>. Null when street, number or postcode is missing.
    /// </summary>
    internal static (string Query, string PostalCode)? LookupQuery(string? address, string? postalFallback)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        // Dots and other signs break the request path ("H.C. Andersens" works as "H C Andersens")
        var text = Regex.Replace(address, @"[^\p{L}\p{N}\s,\-]", " ");
        var house = Regex.Match(text, @"^\s*(?<street>[^\d,]*\p{L}[^\d,]*?)\s+(?<no>\d{1,4})\s*(?<letter>\p{L})?(?=[^\p{L}\p{N}]|$)");
        if (!house.Success) return null;
        var postal = Regex.Match(text[(house.Index + house.Length)..], @"(?<!\d)\d{4}(?!\d)");
        var code = postal.Success ? postal.Value : AreaFromPostalCode(postalFallback) != null ? postalFallback!.Trim() : null;
        if (code == null || AreaFromPostalCode(code) == null) return null;
        var street = Regex.Replace(house.Groups["street"].Value, @"\s+", " ").Trim().Trim('-', ' ');
        return ($"{street} {house.Groups["no"].Value}{house.Groups["letter"].Value} {code}", code);
    }

    /// <summary>Asks which grid company supplies the address. Null when the address is not found.</summary>
    public static async Task<GridCompanyFound?> LookupAddressAsync(HttpClient http, string query, CancellationToken cancel = default)
    {
        using var response = await http.GetAsync(LookupApi + Uri.EscapeDataString(query), cancel);
        response.EnsureSuccessStatusCode();
        return ParseLookup(await response.Content.ReadAsStringAsync(cancel));
    }

    internal static GridCompanyFound? ParseLookup(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("name").GetString() is not { Length: > 0 } name) return null;
        string? Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new GridCompanyFound(name.Trim(), Text("def")?.Trim() ?? "", Text("website"));
    }

    /// <summary>
    /// The name in Energinet's list for the company the lookup found. The two spell names differently ("Elnetselskabet N1"
    /// vs "N1 A/S - 344"), and companies with several grid areas carry the area number in Energinet's name.
    /// </summary>
    internal static string? MatchOwner(GridCompanyFound found, IEnumerable<string> owners)
    {
        var area = int.TryParse(found.Area, out var a) ? a : (int?)null;
        var key = NameKey(found.Name);
        var split = owners.Select(o => (Owner: o, Parts: SplitArea(o))).ToList();
        var byName = split.Where(x => NameKey(x.Parts.Name) == key).ToList();
        if (byName.Count == 1) return byName[0].Owner;
        if (byName.Count > 1 && area != null && byName.Where(x => x.Parts.Area == area).ToList() is [var exact]) return exact.Owner;
        // The area number alone, when only one company carries it
        if (area != null && split.Where(x => x.Parts.Area == area).ToList() is [var onlyArea]) return onlyArea.Owner;
        return null;
    }

    private static (string Name, int? Area) SplitArea(string owner)
    {
        var m = Regex.Match(owner, @"^(?<name>.*?)\s*-\s*(?<area>\d{3})\s*$");
        return m.Success ? (m.Groups["name"].Value, int.Parse(m.Groups["area"].Value, CultureInfo.InvariantCulture)) : (owner, null);
    }

    private static readonly HashSet<string> NameNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "a/s", "as", "amba", "a.m.b.a", "a.m.b.a.", "a", "m", "b", "net", "elnet", "el-net", "elnetselskabet"
    };

    /// <summary>A company name without legal form and "net" words, lower case and letters/digits only: "KONSTANT Net A/S" → "konstant".</summary>
    internal static string NameKey(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !NameNoise.Contains(w))
            .SelectMany(w => w.ToLowerInvariant().Where(char.IsLetterOrDigit)));

    /// <summary>Postal codes 1000-4999 are east of the Great Belt (DK2), the rest in DK1.</summary>
    public static string? AreaFromPostalCode(string? postalCode) =>
        int.TryParse(postalCode?.Trim(), out var pc) && pc is >= 1000 and <= 9999
            ? (pc <= 4999 ? "DK2" : "DK1")
            : null;
}
