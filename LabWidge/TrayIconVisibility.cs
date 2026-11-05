using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>
/// Whether Windows shows LabWidge's bolt by the clock or keeps it under ^. Only read – the choice is the user's.
/// Windows 11 stores it per program in HKCU\Control Panel\NotifyIconSettings (IsPromoted = 1 means by the clock).
/// Windows 10 keeps it in an encoded stream, but with "Always show all icons" off (the default) a new icon goes under ^.
/// </summary>
internal static class TrayIconVisibility
{
    private const string Key = @"Control Panel\NotifyIconSettings";
    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    private static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>True when the bolt is (most likely) under ^.</summary>
    public static bool IsHidden() => IsHidden(Application.ExecutablePath, ReadEntries(), Environment.OSVersion.Version, ReadAutoTray());

    /// <summary>The decision, apart from the registry so it can be tested. <paramref name="autoTray"/> is Windows 10's EnableAutoTray.</summary>
    internal static bool IsHidden(string exePath, IEnumerable<(string Path, bool Promoted)> entries, Version os, int? autoTray)
    {
        if (os.Major < 10) return false;
        // Windows 10: missing or 1 means only some icons are shown by the clock, and a new one is not among them
        if (os.Build < 22000) return autoTray != 0;
        var mine = entries.Where(e => SamePath(e.Path, exePath)).ToList();
        // Windows writes the entry when the icon is first shown; until then a new icon goes under ^
        return mine.Count == 0 || !mine.Any(e => e.Promoted);
    }

    /// <summary>What to do in the window <see cref="OpenSettings"/> opens.</summary>
    public static string HowToShow => IsWindows11
        ? L.T("Turn LabWidge on under \"Other system tray icons\" to keep it by the clock.",
              "Slå LabWidge til under \"Andre ikoner i systembakken\" for at have det ved uret.")
        : L.T("Set LabWidge to \"Show icon and notifications\" to keep it by the clock.",
              "Sæt LabWidge til \"Vis ikon og meddelelser\" for at have det ved uret.");

    /// <summary>
    /// Opens the window where the bolt is turned on by the clock: Windows 11's taskbar settings, or on Windows 10
    /// "Notification Area Icons", which lists LabWidge with a choice of its own.
    /// </summary>
    public static void OpenSettings()
    {
        try
        {
            if (IsWindows11) Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", "shell:::{05d7b0f4-2121-4eff-bf6b-ed3f69b894d9}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not open the tray icon settings: {ex.Message}");
        }
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

    private static int? ReadAutoTray()
    {
        try
        {
            using var explorer = Registry.CurrentUser.OpenSubKey(ExplorerKey);
            return explorer?.GetValue("EnableAutoTray") as int?;
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not read the tray icon settings: {ex.Message}");
            return null;
        }
    }

    private static List<(string, bool)> ReadEntries()
    {
        var entries = new List<(string, bool)>();
        if (!IsWindows11) return entries;
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
