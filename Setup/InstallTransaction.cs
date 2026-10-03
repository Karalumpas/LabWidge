#nullable enable
using System;
using System.IO;

namespace LabWidgeSetup
{
    /// <summary>Switches a finished package in and keeps the old installation until the switch is complete.</summary>
    internal static class InstallTransaction
    {
        public static void Recover(string installDir)
        {
            var backup = installDir + ".backup";
            if (!Directory.Exists(backup)) return;
            if (!Directory.Exists(installDir))
            {
                Directory.Move(backup, installDir);
                return;
            }
            PreserveWebView(backup, installDir);
            // A completed installation can be used even if antivirus briefly locks the backup.
            try { Directory.Delete(backup, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public static void Commit(string installDir, string staging, Action<string, string>? move = null)
        {
            if (!File.Exists(Path.Combine(staging, "LabWidge.exe")))
                throw new InvalidOperationException("The installation package is missing LabWidge.exe.");
            var backup = installDir + ".backup";
            if (Directory.Exists(backup))
                throw new IOException("The previous installation backup is locked. Try again later.");

            move = move ?? Directory.Move;
            var hadInstallation = Directory.Exists(installDir);
            if (hadInstallation) move(installDir, backup);
            try
            {
                move(staging, installDir);
                if (hadInstallation) PreserveWebView(backup, installDir);
            }
            catch
            {
                if (hadInstallation)
                {
                    // Directory.Move moves the whole folder; there is no half-done file copy to restore.
                    if (Directory.Exists(installDir)) Directory.Move(installDir, staging);
                    Directory.Move(backup, installDir);
                }
                throw;
            }
            if (hadInstallation)
            {
                try { Directory.Delete(backup, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static void PreserveWebView(string oldDir, string newDir)
        {
            var oldProfile = Path.Combine(oldDir, "webview");
            var newProfile = Path.Combine(newDir, "webview");
            if (Directory.Exists(oldProfile) && !Directory.Exists(newProfile))
                Directory.Move(oldProfile, newProfile);
        }
    }
}
