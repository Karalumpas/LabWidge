#nullable enable
using System.Globalization;
using Microsoft.Win32;

/// <summary>
/// The app is available in English and Danish. Every user-facing text is written as <c>L.T("English", "Danish")</c>
/// at the place it is shown, so both versions are kept side by side and a missing translation is impossible.
/// The language is chosen in the installer, can be changed under Settings → Widget and takes effect after a restart.
/// </summary>
internal static class L
{
    public const string English = "en";
    public const string Danish = "da";

    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-GB");
    private static readonly CultureInfo DanishCulture = CultureInfo.GetCultureInfo("da-DK");

    /// <summary>The language in use: <see cref="English"/> or <see cref="Danish"/>.</summary>
    public static string Current { get; private set; } = English;

    public static bool IsDanish => Current == Danish;

    /// <summary>Dates, times and numbers follow the language: "Friday 3 Oct" and 1.5 – or "fredag 3. okt." and 1,5.</summary>
    public static CultureInfo Culture => IsDanish ? DanishCulture : EnglishCulture;

    public static string T(string english, string danish) => IsDanish ? danish : english;

    public static void Use(string? language) => Current = Normalize(language) ?? English;

    public static string? Normalize(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "en" or "english" => English,
        "da" or "danish" or "dansk" => Danish,
        _ => null
    };

    /// <summary>The language when nothing else is chosen: Danish on a Danish Windows, otherwise English.</summary>
    public static string FromWindows() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "da" ? Danish : English;

    // The installer stores the chosen language here; the app takes it over into its settings at the next start.
    private const string RegistryKey = @"Software\LabWidge";
    private const string RegistryValue = "Language";

    public static void RememberInstallerChoice(string language)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKey);
            key?.SetValue(RegistryValue, Normalize(language) ?? English);
        }
        catch
        {
            // Without it the app falls back to its own rules
        }
    }

    /// <summary>Reads and removes the language chosen in the installer. Null if none is waiting.</summary>
    public static string? TakeInstallerChoice()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
            var value = Normalize(key?.GetValue(RegistryValue) as string);
            key?.DeleteValue(RegistryValue, throwOnMissingValue: false);
            return value;
        }
        catch
        {
            return null;
        }
    }
}
