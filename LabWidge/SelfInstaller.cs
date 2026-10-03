using System.Diagnostics;
using System.Windows.Forms;

/// <summary>
/// A self-contained exe installs itself: started from somewhere else (e.g. Downloads), it is copied to
/// %LOCALAPPDATA%\LabWidge and started from there. A newer exe replaces the installed one.
/// Development builds (not single-file) run as they are.
/// </summary>
internal static class SelfInstaller
{
    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LabWidge");

    public static string InstalledExe => Path.Combine(InstallDir, "LabWidge.exe");

    /// <summary>Single-file exes have no assembly path on disk.</summary>
#pragma warning disable IL3000 // An empty path is exactly what identifies single-file
    private static bool IsSingleFile => string.IsNullOrEmpty(typeof(SelfInstaller).Assembly.Location);
#pragma warning restore IL3000

    public static bool RedirectToInstalledCopy(string[] args)
    {
        var current = Environment.ProcessPath;
        if (current == null || !IsSingleFile) return false;
        if (string.Equals(Path.GetFullPath(current), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            Directory.CreateDirectory(InstallDir);
            if (NeedsCopy(current))
            {
                StopInstalledInstance();
                RemoveFrameworkDependentFiles();
                CopyWithRetry(current, InstalledExe);
                Logger.Info($"Installed version {FileVersion(InstalledExe)} from {current}.");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = InstalledExe,
                Arguments = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                WorkingDirectory = InstallDir,
                UseShellExecute = false
            });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Self-installation failed: {ex.Message}");
            MessageBox.Show(
                L.T($"LabWidge could not be installed in\n{InstallDir}\n\n{ex.Message}\n\nThe app starts from its current location instead.",
                    $"LabWidge kunne ikke installeres i\n{InstallDir}\n\n{ex.Message}\n\nAppen startes fra den nuværende placering i stedet."),
                "LabWidge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private static bool NeedsCopy(string source)
    {
        if (!File.Exists(InstalledExe)) return true;
        var src = new FileInfo(source);
        var dst = new FileInfo(InstalledExe);
        return src.Length != dst.Length || FileVersion(source) != FileVersion(InstalledExe) || src.LastWriteTimeUtc > dst.LastWriteTimeUtc;
    }

    private static string FileVersion(string path) => FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";

    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LabWidge";

    /// <summary>Registers the app under Settings → Apps, so it can be uninstalled.</summary>
    public static void RegisterUninstallEntry()
    {
        var current = Environment.ProcessPath;
        if (current == null || !string.Equals(Path.GetFullPath(current), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
            key.SetValue("DisplayName", "LabWidge");
            key.SetValue("DisplayVersion", FileVersionInfo.GetVersionInfo(current).ProductVersion?.Split('+')[0] ?? "");
            key.SetValue("Publisher", "LabWidge");
            key.SetValue("DisplayIcon", current);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{current}\" --uninstall");
            key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
            var bytes = Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            key.SetValue("EstimatedSize", (int)(bytes / 1024), Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not register the uninstaller: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        if (MessageBox.Show(L.T("Uninstall LabWidge?", "Vil du afinstallere LabWidge?"), L.T("Uninstall LabWidge", "Afinstallér LabWidge"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        StopInstalledInstance();
        StartupRegistration.Apply(false);
        ToastHelper.RemoveRegistration();
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false); } catch { /* ignoreres */ }

        if (MessageBox.Show(L.T("Also delete your settings, saved tokens (Cloudflare, Home Assistant and Proxmox) and the Home Assistant login?",
                                "Skal dine indstillinger, gemte tokens (Cloudflare, Home Assistant og Proxmox) og Home Assistant-login også slettes?"),
                L.T("Uninstall LabWidge", "Afinstallér LabWidge"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            CredentialStore.DeleteToken();
            CredentialStore.DeleteHomeAssistantToken();
            CredentialStore.DeleteProxmoxSecret();
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabWidge");
            foreach (var dir in new[] { dataDir, WebViewProfile.DataDir })
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* ignoreres */ }
            }
        }

        // The exe itself runs from the folder; delete it once the process has ended
        Process.Start(UninstallCleanup.StartInfo(InstallDir, Environment.ProcessId));
        MessageBox.Show(L.T("LabWidge has been uninstalled.", "LabWidge er afinstalleret."), "LabWidge", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Closes a running installed version so it can be updated.</summary>
    private static void StopInstalledInstance()
    {
        foreach (var p in Process.GetProcessesByName("LabWidge"))
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                var path = p.MainModule?.FileName;
                if (path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch
            {
                // No access to the process; the copy then fails with an explanation.
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    /// <summary>Leftovers from an earlier framework-dependent installation (install.ps1 before v1.2).</summary>
    private static void RemoveFrameworkDependentFiles()
    {
        foreach (var pattern in new[] { "*.dll", "*.deps.json", "*.runtimeconfig.json", "*.pdb" })
        {
            foreach (var file in Directory.EnumerateFiles(InstallDir, pattern))
            {
                try { File.Delete(file); } catch { /* ignoreres */ }
            }
        }
    }

    private static void CopyWithRetry(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(500); // the old process is closing
            }
        }
    }
}
