using System.Net.Http;
using System.Windows.Forms;
using Windows.Services.Store;

internal enum UpdateChannel { GitHub, Store }

/// <summary>A newer version that a check found. <see cref="Payload"/> is what the source needs to install it.</summary>
internal sealed record UpdateOffer(Version Version, string Notes, long Size, object Payload);

/// <summary>Where updates come from: GitHub for the installed EXE, Microsoft Store for the Store package.</summary>
internal interface IUpdateSource
{
    UpdateChannel Channel { get; }
    /// <summary>The newer version, or null when this is the latest. Throws when the source cannot be reached.</summary>
    Task<UpdateOffer?> CheckAsync(CancellationToken cancel);
    /// <summary>Downloads and installs <paramref name="offer"/>, reporting progress from 0 to 1. Returns false when the user cancelled.
    /// For GitHub the downloaded installer is handed to <see cref="AppUpdates.PendingInstaller"/>; it runs when Settings has closed.
    /// For the Store, Microsoft Store installs the update itself and Windows restarts the app.</summary>
    Task<bool> InstallAsync(UpdateOffer offer, IWin32Window owner, IProgress<double> progress, CancellationToken cancel);
}

internal static class AppUpdates
{
    /// <summary>The source for this installation. Tests replace it with a fake.</summary>
    public static Func<IUpdateSource> Factory { get; set; } =
        () => AppPackage.IsPackaged ? new StoreUpdateSource() : new GitHubUpdateSource();

    /// <summary>A verified installer the user chose to run. The tray app starts it once the settings window has closed.</summary>
    public static string? PendingInstaller { get; set; }

    public const string StoreProductId = "9PN5S2Z8PTQM";
    public static string StorePage => $"ms-windows-store://pdp/?ProductId={StoreProductId}";
}

internal sealed class GitHubUpdateSource : IUpdateSource
{
    private static readonly HttpClient Http = HttpClientFactory.Create(TimeSpan.FromSeconds(15));
    public UpdateChannel Channel => UpdateChannel.GitHub;

    public async Task<UpdateOffer?> CheckAsync(CancellationToken cancel)
    {
        var release = await UpdateService.FetchLatestAsync(Http).WaitAsync(cancel);
        return release != null && UpdateService.IsNewer(release)
            ? new UpdateOffer(release.Version, release.Notes, release.Size, release)
            : null;
    }

    public async Task<bool> InstallAsync(UpdateOffer offer, IWin32Window owner, IProgress<double> progress, CancellationToken cancel)
    {
        var release = (ReleaseInfo)offer.Payload;
        try
        {
            var path = await UpdateService.DownloadAsync(release,
                new Progress<(long Received, long Total)>(p => progress.Report(p.Total > 0 ? (double)p.Received / p.Total : 0)), cancel);
            AppUpdates.PendingInstaller = path;
            return true;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return false; }
    }
}

/// <summary>Asks Microsoft Store for an update of this package and lets the Store download and install it.</summary>
internal sealed class StoreUpdateSource : IUpdateSource
{
    private StoreContext? _context;
    public UpdateChannel Channel => UpdateChannel.Store;

    private StoreContext Context(IWin32Window? owner)
    {
        if (_context == null)
        {
            _context = StoreContext.GetDefault();
            // The Store shows its own confirmation and progress; a desktop app must tell it which window owns them
            if (owner != null) WinRT.Interop.InitializeWithWindow.Initialize(_context, owner.Handle);
        }
        return _context;
    }

    public async Task<UpdateOffer?> CheckAsync(CancellationToken cancel)
    {
        var updates = await Context(null).GetAppAndOptionalStorePackageUpdatesAsync().AsTask(cancel);
        if (updates.Count == 0) return null;
        var v = updates[0].Package.Id.Version;
        return new UpdateOffer(new Version(v.Major, v.Minor, v.Build), "", 0, updates);
    }

    public async Task<bool> InstallAsync(UpdateOffer offer, IWin32Window owner, IProgress<double> progress, CancellationToken cancel)
    {
        // A context made for the check has no window yet; the install needs one for the Store's dialogs
        _context = null;
        var context = Context(owner);
        var updates = (IReadOnlyList<StorePackageUpdate>)offer.Payload;
        var operation = context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
        operation.Progress = (_, status) => progress.Report(Math.Clamp(status.TotalDownloadProgress, 0, 1));
        var result = await operation.AsTask(cancel);
        return result.OverallState switch
        {
            StorePackageUpdateState.Completed => true,
            StorePackageUpdateState.Canceled => false,
            var state => throw new InvalidOperationException(L.T($"Microsoft Store could not install the update ({state}).",
                                                                 $"Microsoft Store kunne ikke installere opdateringen ({state})."))
        };
    }
}
