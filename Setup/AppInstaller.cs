using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace LabWidgeSetup
{
    /// <summary>Unpacks the embedded LabWidge.exe into %LOCALAPPDATA%\LabWidge.</summary>
    internal static class AppInstaller
    {
        public static string InstallDir { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LabWidge");

        public static string InstalledExe => Path.Combine(InstallDir, "LabWidge.exe");

        public static string PackagedVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        /// <summary>The installed version – also an IpTrayWidget from before the rename, which this installation replaces.</summary>
        public static string InstalledVersion =>
            File.Exists(InstalledExe) ? FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion?.Split('+')[0]
            : File.Exists(LegacyExe) ? FileVersionInfo.GetVersionInfo(LegacyExe).ProductVersion?.Split('+')[0]
            : null;

        public static void Install()
        {
            using (var mutex = new Mutex(false, "Local\\LabWidge.Setup"))
            {
                try { mutex.WaitOne(); } catch (AbandonedMutexException) { /* Ownership was taken over. */ }
                try { InstallCore(); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static void InstallCore()
        {
            // Also recover after a power cut between the two folder switches.
            Retry(() => InstallTransaction.Recover(InstallDir));

            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.zip"))
            {
                if (resource == null) throw new InvalidOperationException(L.T("The installation package is incomplete (app.zip is missing).", "Installationspakken er ufuldstændig (app.zip mangler)."));

                // Unpack to a temporary folder first, so an interrupted installation does not leave half an app
                var staging = InstallDir + ".staging";
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                using (var zip = new ZipArchive(resource, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        var target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                        if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(L.T("Invalid path in the installation package.", "Ugyldig sti i installationspakken."));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var input = entry.Open())
                        using (var output = File.Create(target))
                        {
                            input.CopyTo(output);
                        }
                    }
                }

                StopRunningInstance();
                Retry(() => InstallTransaction.Commit(InstallDir, staging));
            }
        }

        private static void Retry(Action action)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 10)
                {
                    Thread.Sleep(500); // the old process is closing
                }
            }
        }

        public static void Launch(bool runSetupGuide)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = InstalledExe,
                Arguments = runSetupGuide ? "--setup" : "",
                WorkingDirectory = InstallDir,
                UseShellExecute = false
            });
        }

        /// <summary>The app was called IpTrayWidget before 1.11 and lived in its own folder. That version is closed too, so LabWidge can take over.</summary>
        private static readonly string LegacyExe =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IpTrayWidget", "IpTrayWidget.exe");

        private static void StopRunningInstance()
        {
            foreach (var p in Process.GetProcessesByName("LabWidge").Concat(Process.GetProcessesByName("IpTrayWidget")))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path != null && (string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(Path.GetFullPath(path), Path.GetFullPath(LegacyExe), StringComparison.OrdinalIgnoreCase)))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch
                {
                    // No access; the move retries.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        /// <summary>
        /// The language of an existing installation (from settings.json), Danish for installations from before 1.11
        /// (the app was Danish-only then), otherwise the Windows display language.
        /// </summary>
        public static string CurrentLanguage()
        {
            foreach (var name in new[] { "LabWidge", "IpTrayWidget" })
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name, "settings.json");
                if (!File.Exists(path)) continue;
                try
                {
                    var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(path), "\"Language\"\\s*:\\s*\"(\\w+)\"");
                    return (match.Success ? L.Normalize(match.Groups[1].Value) : null) ?? L.Danish;
                }
                catch
                {
                    return L.Danish;
                }
            }
            return L.FromWindows();
        }

        /// <summary>Settings from IpTrayWidget count too – the app moves them itself at its first start.</summary>
        public static bool SettingsExist =>
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabWidge", "settings.json"))
            || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IpTrayWidget", "settings.json"));
    }
}
