using System;
using System.Windows.Forms;

namespace LabWidgeSetup
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Older widgets start Setup with the installation folder as working directory.
            // Release it also with --silent, before the old program folder is moved to the backup.
            InstallerEnvironment.Prepare();

            // The app updates itself with --silent: no windows, just swap the files and start again.
            // If that fails, we fall back to the normal installer window.
            if (args != null && Array.IndexOf(args, "--silent") >= 0 && TryUpdateSilently())
            {
                return;
            }

            // The installer opens in the language already in use, or the Windows display language
            L.Use(AppInstaller.CurrentLanguage());
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }

        private static bool TryUpdateSilently()
        {
            try
            {
                // The runtime is only missing if something went wrong – then the user must see the dialog
                if (RuntimeInstaller.FindDesktopRuntime() == null) return false;

                AppInstaller.Install();
                AppInstaller.Launch(runSetupGuide: false);
                return true;
            }
            catch (AppBlockedException)
            {
                // The working version was not touched: start it again quietly – the next update check tries again
                try { AppInstaller.Launch(runSetupGuide: false); } catch { /* blocked too – nothing more to do silently */ }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
