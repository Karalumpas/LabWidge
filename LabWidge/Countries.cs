#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>Where the spot prices for a price area come from.</summary>
internal enum PriceSource
{
    EnergiDataService, // Energinet, Denmark
    Elprisetjustnu,    // Sweden
    Hvakosterstrommen, // Norway
    Elering,           // Finland and the Baltics
    Awattar,           // Germany/Luxembourg and Austria
    EnergyZero,        // Netherlands
    Pse,               // Poland
    Omie               // Spain and Portugal
}

/// <summary>The unit prices are shown in: e.g. øre (1/100 DKK) or ct (1/100 EUR).</summary>
internal sealed class PriceUnit
{
    public PriceUnit(string currency, string symbol, double perMajor, int decimals)
    {
        Currency = currency;
        Symbol = symbol;
        PerMajor = perMajor;
        Decimals = decimals;
    }

    /// <summary>ISO 4217 code of the currency the source delivers, e.g. "DKK".</summary>
    public string Currency { get; }
    /// <summary>What the widget writes after a price, e.g. "øre" or "ct".</summary>
    public string Symbol { get; }
    /// <summary>Units per whole currency unit: 100 for øre and cent.</summary>
    public double PerMajor { get; }
    public int Decimals { get; }

    public string Format(double value, IFormatProvider culture) =>
        value.ToString(Decimals == 0 ? "0" : "0." + new string('0', Decimals), culture);

    public string PerKwh => Symbol + "/kWh";
}

/// <summary>A bidding zone – the area a spot price applies to.</summary>
internal sealed class PriceArea
{
    public PriceArea(string code, string englishName, string danishName, PriceSource source)
    {
        Code = code;
        _englishName = englishName;
        _danishName = danishName;
        Source = source;
    }

    private readonly string _englishName, _danishName;

    /// <summary>The code stored in the settings, e.g. "DK1", "SE3", "DE-LU".</summary>
    public string Code { get; }
    /// <summary>Region name shown in the widget header, e.g. "Stockholm". Empty for countries with one area.</summary>
    public string ShortName => L.T(_englishName, _danishName);
    public PriceSource Source { get; }
}

internal sealed class Country
{
    public Country(string code, string nativeName, string englishName, string danishName, PriceUnit unit, double vatPercent,
                   params PriceArea[] areas)
    {
        Code = code;
        NativeName = nativeName;
        EnglishName = englishName;
        DanishName = danishName;
        Unit = unit;
        VatPercent = vatPercent;
        Areas = areas;
    }

    /// <summary>ISO 3166 code, e.g. "DK".</summary>
    public string Code { get; }
    public string NativeName { get; }
    public string EnglishName { get; }
    public string DanishName { get; }
    public PriceUnit Unit { get; }
    /// <summary>The usual VAT on household electricity. The user can change it in the settings.</summary>
    public double VatPercent { get; }
    public IReadOnlyList<PriceArea> Areas { get; }

    public string Name => L.T(EnglishName, DanishName);

    /// <summary>"Sverige (Sweden)" – the native name, with the translation when it differs.</summary>
    public string DisplayName => NativeName == Name ? Name : $"{NativeName} ({Name})";

    public PriceArea DefaultArea => Areas[0];

    public PriceArea AreaOrDefault(string? code) => Areas.FirstOrDefault(a => a.Code == code) ?? DefaultArea;

    public override string ToString() => DisplayName;
}

/// <summary>
/// The countries LabWidge can show electricity prices for. Shared with the installer, so it only uses C# that
/// also builds for .NET Framework 4.8.
/// </summary>
internal static class Countries
{
    /// <summary>Stored when the user's country has no electricity prices – the price section is then hidden.</summary>
    public const string Other = "OTHER";

    private static readonly PriceUnit Dkk = new PriceUnit("DKK", "øre", 100, 0);
    private static readonly PriceUnit Sek = new PriceUnit("SEK", "öre", 100, 0);
    private static readonly PriceUnit Nok = new PriceUnit("NOK", "øre", 100, 0);
    private static readonly PriceUnit Eur = new PriceUnit("EUR", "ct", 100, 1);
    private static readonly PriceUnit Pln = new PriceUnit("PLN", "gr", 100, 0);

    public static readonly IReadOnlyList<Country> All = new[]
    {
        new Country("DK", "Danmark", "Denmark", "Danmark", Dkk, 25,
            new PriceArea("DK1", "West", "Vest", PriceSource.EnergiDataService),
            new PriceArea("DK2", "East", "Øst", PriceSource.EnergiDataService)),
        new Country("SE", "Sverige", "Sweden", "Sverige", Sek, 25,
            new PriceArea("SE1", "Luleå", "Luleå", PriceSource.Elprisetjustnu),
            new PriceArea("SE2", "Sundsvall", "Sundsvall", PriceSource.Elprisetjustnu),
            new PriceArea("SE3", "Stockholm", "Stockholm", PriceSource.Elprisetjustnu),
            new PriceArea("SE4", "Malmö", "Malmö", PriceSource.Elprisetjustnu)),
        new Country("NO", "Norge", "Norway", "Norge", Nok, 25,
            new PriceArea("NO1", "Oslo", "Oslo", PriceSource.Hvakosterstrommen),
            new PriceArea("NO2", "Kristiansand", "Kristiansand", PriceSource.Hvakosterstrommen),
            new PriceArea("NO3", "Trondheim", "Trondheim", PriceSource.Hvakosterstrommen),
            new PriceArea("NO4", "Tromsø", "Tromsø", PriceSource.Hvakosterstrommen),
            new PriceArea("NO5", "Bergen", "Bergen", PriceSource.Hvakosterstrommen)),
        new Country("FI", "Suomi", "Finland", "Finland", Eur, 25.5, new PriceArea("FI", "", "", PriceSource.Elering)),
        new Country("EE", "Eesti", "Estonia", "Estland", Eur, 24, new PriceArea("EE", "", "", PriceSource.Elering)),
        new Country("LV", "Latvija", "Latvia", "Letland", Eur, 21, new PriceArea("LV", "", "", PriceSource.Elering)),
        new Country("LT", "Lietuva", "Lithuania", "Litauen", Eur, 21, new PriceArea("LT", "", "", PriceSource.Elering)),
        new Country("DE", "Deutschland", "Germany", "Tyskland", Eur, 19, new PriceArea("DE-LU", "", "", PriceSource.Awattar)),
        new Country("LU", "Lëtzebuerg", "Luxembourg", "Luxembourg", Eur, 8, new PriceArea("DE-LU", "", "", PriceSource.Awattar)),
        new Country("AT", "Österreich", "Austria", "Østrig", Eur, 20, new PriceArea("AT", "", "", PriceSource.Awattar)),
        new Country("NL", "Nederland", "Netherlands", "Holland", Eur, 21, new PriceArea("NL", "", "", PriceSource.EnergyZero)),
        new Country("PL", "Polska", "Poland", "Polen", Pln, 23, new PriceArea("PL", "", "", PriceSource.Pse)),
        new Country("ES", "España", "Spain", "Spanien", Eur, 21, new PriceArea("ES", "", "", PriceSource.Omie)),
        new Country("PT", "Portugal", "Portugal", "Portugal", Eur, 23, new PriceArea("PT", "", "", PriceSource.Omie)),
    };

    /// <summary>The country for a code, or null for <see cref="Other"/> and unknown codes.</summary>
    public static Country? Find(string? code) =>
        All.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The supported country Windows is set to, otherwise <see cref="Other"/>.</summary>
    public static string FromWindows()
    {
        try
        {
            return Find(RegionInfo.CurrentRegion.TwoLetterISORegionName)?.Code ?? Other;
        }
        catch
        {
            return Other;
        }
    }

    /// <summary>A stored value made safe: a known country code or <see cref="Other"/>.</summary>
    public static string Normalize(string? code) => Find(code)?.Code ?? Other;

    // The installer stores the chosen country next to the language; the app takes it over at the next start.
    private const string RegistryKey = @"Software\LabWidge";
    private const string RegistryValue = "Country";

    public static void RememberInstallerChoice(string country)
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryKey))
                key?.SetValue(RegistryValue, Normalize(country));
        }
        catch
        {
            // Without it the app falls back to its own rules
        }
    }

    /// <summary>Reads and removes the country chosen in the installer. Null if none is waiting.</summary>
    public static string? TakeInstallerChoice()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true))
            {
                if (!(key?.GetValue(RegistryValue) is string value)) return null;
                key.DeleteValue(RegistryValue, throwOnMissingValue: false);
                return Normalize(value);
            }
        }
        catch
        {
            return null;
        }
    }
}
