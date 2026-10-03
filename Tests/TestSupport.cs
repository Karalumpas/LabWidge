// Regression tests use the real services, but must not write the user's log or start the app.
internal static class Logger
{
    public static void Error(string message) { }
    public static void Info(string message) { }
}

internal static class SelfInstaller
{
    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LabWidge");
    public static string InstalledExe => Path.Combine(InstallDir, "LabWidge.exe");
}
