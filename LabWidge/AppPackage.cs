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
    /// The folder that holds what LabWidge writes under %APPDATA%\<paramref name="name"/>. A packaged app's writes there are
    /// redirected by Windows to the package's own folder, so other programs such as Explorer must be shown that one.
    /// </summary>
    public static string RoamingDataDir(string name)
    {
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name);
        if (!IsPackaged) return roaming;
        try
        {
            return Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "Roaming", name);
        }
        catch (Exception)
        {
            return roaming;
        }
    }
}
