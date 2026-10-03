using System.Diagnostics;
using Microsoft.Win32;

/// <summary>
/// The app was called IpTrayWidget before 1.11. The first time LabWidge starts, the old setup is moved over so
/// nobody loses settings, tokens or the Home Assistant login. Everything is idempotent: a step that fails
/// (e.g. a locked file) is retried at the next start, and whatever already exists under LabWidge is not overwritten.
/// </summary>
internal static class LegacyMigration
{
    private const string OldName = "IpTrayWidget";

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string OldDataDir => Path.Combine(Roaming, OldName);
    private static string NewDataDir => Path.Combine(Roaming, "LabWidge");
    private static string OldInstallDir => Path.Combine(Local, OldName);
    private static string OldWebViewDataDir => Path.Combine(Local, OldName + "Data");

    /// <summary>Called first in Main – before anything is written to the log or the settings are read.</summary>
    public static void Run()
    {
        var dataExisted = Directory.Exists(OldDataDir);
        if (!dataExisted && !Directory.Exists(OldInstallDir) && !Directory.Exists(OldWebViewDataDir)) return;

        StopOldApp();
        // Settings and log are moved before the log is used, so settings.json is in place when it is loaded
        MergeDirectory(OldDataDir, NewDataDir);
        if (dataExisted) Logger.Info("Moving the setup from IpTrayWidget to LabWidge.");

        CredentialStore.MigrateFromIpTrayWidget();
        MergeDirectory(OldWebViewDataDir, WebViewProfile.DataDir);
        // Before 1.6.1 the Home Assistant login lived in the installation folder
        MergeDirectory(Path.Combine(OldInstallDir, "webview"), WebViewProfile.Path);
        RemoveOldRegistration();
        RemoveOldInstallDir();
    }

    /// <summary>Closes an IpTrayWidget that is still running from the old installation folder.</summary>
    private static void StopOldApp()
    {
        foreach (var p in Process.GetProcessesByName(OldName))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path == null || !path.StartsWith(OldInstallDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill();
                p.WaitForExit(5000);
            }
            catch
            {
                // No access – the folder is removed at a later start
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private static void MergeDirectory(string source, string target)
    {
        try
        {
            DirectoryMerge.Merge(source, target);
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not move {source} to {target}: {ex.Message}");
        }
    }

    /// <summary>Removes IpTrayWidget's autostart, uninstall entry, shortcut and notification registration.</summary>
    private static void RemoveOldRegistration()
    {
        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
                run?.DeleteValue(OldName, throwOnMissingValue: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + OldName, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\" + OldName + ".App", throwOnMissingSubKey: false);
            var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", OldName + ".lnk");
            if (File.Exists(shortcut)) File.Delete(shortcut);
        }
        catch (Exception ex)
        {
            Logger.Error($"IpTrayWidget's registration could not be removed: {ex.Message}");
        }
    }

    /// <summary>The old program folder is deleted, unless the app itself runs from there (e.g. a development copy).</summary>
    private static void RemoveOldInstallDir()
    {
        if (!Directory.Exists(OldInstallDir)) return;
        if (AppContext.BaseDirectory.StartsWith(OldInstallDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            Directory.Delete(OldInstallDir, recursive: true);
            Logger.Info("The old IpTrayWidget installation was removed.");
        }
        catch (Exception ex)
        {
            Logger.Error($"The old IpTrayWidget folder could not be removed: {ex.Message}");
        }
    }
}
