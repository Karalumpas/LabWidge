using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>
/// Whether Windows 11 shows LabWidge's bolt by the clock or keeps it under ^. Windows stores this per program in
/// HKCU\Control Panel\NotifyIconSettings: IsPromoted = 1 means by the clock. Only read – the choice is the user's.
/// </summary>
internal static class TrayIconVisibility
{
    private const string Key = @"Control Panel\NotifyIconSettings";

    /// <summary>True when the bolt is (most likely) under ^. False when it is by the clock or Windows has no such setting.</summary>
    public static bool IsHidden() => IsHidden(Application.ExecutablePath, ReadEntries(), Environment.OSVersion.Version);

    /// <summary>The decision, apart from the registry so it can be tested.</summary>
    internal static bool IsHidden(string exePath, IEnumerable<(string Path, bool Promoted)> entries, Version os)
    {
        if (os.Major < 10 || os.Build < 22000) return false; // Windows 10 shows its own "hidden icons" choice differently
        var mine = entries.Where(e => SamePath(e.Path, exePath)).ToList();
        // Windows writes the entry when the icon is first shown; until then a new icon goes under ^
        return mine.Count == 0 || !mine.Any(e => e.Promoted);
    }

    /// <summary>Opens Windows' taskbar settings, where "Other system tray icons" turns the bolt on by the clock.</summary>
    public static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Error($"Could not open the taskbar settings: {ex.Message}"); }
    }

    /// <summary>
    /// The stored path can start with a known folder's id, e.g. "{6D809377-...}\WindowsApps\...\LabWidge.exe"
    /// for Program Files, so the part after the id is compared with the end of our path.
    /// </summary>
    internal static bool SamePath(string stored, string exePath)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        if (stored.StartsWith('{') && stored.IndexOf("}\\", StringComparison.Ordinal) is var end and > 0)
            return exePath.EndsWith(stored[(end + 1)..], StringComparison.OrdinalIgnoreCase);
        return string.Equals(stored, exePath, StringComparison.OrdinalIgnoreCase);
    }

    private static List<(string, bool)> ReadEntries()
    {
        var entries = new List<(string, bool)>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Key);
            if (root == null) return entries;
            foreach (var name in root.GetSubKeyNames())
            {
                using var icon = root.OpenSubKey(name);
                if (icon?.GetValue("ExecutablePath") is not string path) continue;
                entries.Add((path, icon.GetValue("IsPromoted") is int promoted && promoted == 1));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not read the tray icon settings: {ex.Message}");
        }
        return entries;
    }
}
