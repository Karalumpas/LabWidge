using System.Runtime.InteropServices;

/// <summary>
/// Whether LabWidge runs from its Microsoft Store (MSIX) package. The Store then installs and updates the app,
/// and Windows starts it at sign-in through the package's startup task instead of the Run key.
/// </summary>
internal static class AppPackage
{
    /// <summary>The startup task's id in Packaging/AppxManifest.xml.</summary>
    public const string StartupTaskId = "LabWidgeStartup";

    /// <summary>The package's own copy of the logo, for the toast header (Packaging/AppxManifest.xml).</summary>
    public const string ToastLogoUri = "ms-appx:///PackageAssets/Square150x150Logo.png";

    public static bool IsPackaged { get; } = Detect();

    private const int AppModelErrorNoPackage = 15700;

    private static bool Detect()
    {
        try
        {
            var length = 0;
            // Without a package Windows answers APPMODEL_ERROR_NO_PACKAGE; with one, that the buffer is too small
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    /// <summary>
    /// The folders that hold what LabWidge keeps under %APPDATA%\<paramref name="name"/>, as other programs such as Explorer
    /// see them. A packaged app's new files there are redirected by Windows to the package's own folder, while files an
    /// earlier GitHub installation left in the ordinary folder stay there and are changed in place (e.g. the log).
    /// The package's folder comes first; the ordinary one is added when it has files the package's folder does not.
    /// </summary>
    public static IReadOnlyList<string> RoamingDataDirs(string name)
    {
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name);
        if (!IsPackaged) return new[] { roaming };
        try
        {
            var own = Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "Roaming", name);
            var ownFiles = Directory.Exists(own)
                ? Directory.EnumerateFiles(own).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string?>(StringComparer.OrdinalIgnoreCase);
            // Inside the package this path shows both folders merged, so a file the package's folder lacks is in the ordinary one
            var onlyOrdinary = Directory.Exists(roaming) && Directory.EnumerateFiles(roaming).Any(f => !ownFiles.Contains(Path.GetFileName(f)));
            var dirs = new List<string>();
            if (Directory.Exists(own)) dirs.Add(own);
            if (onlyOrdinary || dirs.Count == 0) dirs.Add(roaming);
            return dirs;
        }
        catch (Exception)
        {
            return new[] { roaming };
        }
    }
}
