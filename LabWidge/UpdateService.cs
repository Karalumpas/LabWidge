using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

/// <summary>A release on GitHub with an installer attached.</summary>
internal sealed record ReleaseInfo(Version Version, string Title, string Notes, string DownloadUrl, long Size, string? Sha256);

/// <summary>
/// Keeps the app up to date through GitHub Releases. Checking only fetches metadata (no download);
/// the installer itself is only downloaded when the user agrees.
/// </summary>
internal static class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Karalumpas/labwidge-releases/releases/latest";
    public const string ReleasesPage = "https://github.com/Karalumpas/labwidge-releases/releases";
    private const string AssetName = "LabWidge-Setup.exe";

    /// <summary>Only GitHub's own hosts may deliver the installer.</summary>
    private static readonly string[] AllowedHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    private static readonly Regex HashNearLabel = new(@"sha-?256[^0-9a-f]{0,20}([0-9a-f]{64})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AnyHash = new(@"\b([0-9a-f]{64})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string DownloadDir => Path.Combine(Path.GetTempPath(), "LabWidge-update");

    /// <summary>
    /// A client of its own for the download: HttpClient.Timeout also covers reading the response body,
    /// so a short timeout would abort a perfectly normal download on a slow connection.
    /// The user cancels with the button in the window instead.
    /// </summary>
    private static readonly HttpClient Downloader = HttpClientFactory.Create(TimeSpan.FromMinutes(20));

    public static Version Current { get; } = ParseVersion(Application.ProductVersion) ?? new Version(0, 0, 0);

    /// <summary>Fetches the latest release. Returns null if it has no installer attached.</summary>
    public static async Task<ReleaseInfo?> FetchLatestAsync(HttpClient http)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await http.SendAsync(request).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // No releases yet – or the repository is private, and then the app cannot see them
            Logger.Info("Version check: GitHub has no visible release.");
            return null;
        }
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = doc.RootElement;

        var tag = Text(root, "tag_name");
        var version = ParseVersion(tag);
        if (version == null) return null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(Text(asset, "name"), AssetName, StringComparison.OrdinalIgnoreCase)) continue;

            var url = Text(asset, "browser_download_url");
            if (url.Length == 0) continue;

            var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
            var notes = Text(root, "body");
            var title = Text(root, "name");

            // GitHub states the asset's checksum as "sha256:<hex>"; otherwise the release text is searched
            var digest = Text(asset, "digest");
            var hash = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : FindHash(notes);

            return new ReleaseInfo(version, title.Length > 0 ? title : tag, notes, url, size, hash);
        }
        return null;
    }

    public static bool IsNewer(ReleaseInfo release) => release.Version > Current;

    /// <summary>
    /// Downloads the installer to a temporary folder and checks it along the way:
    /// https from GitHub only, the expected size and that the file's SHA-256 checksum matches.
    /// Returns the path of the downloaded file.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release,
                                                   IProgress<(long Received, long Total)>? progress,
                                                   CancellationToken token)
    {
        var uri = new Uri(release.DownloadUrl);
        if (uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(L.T("The update is not on GitHub – the download was refused.", "Opdateringen ligger ikke på GitHub – downloaden blev afvist."));
        }

        CleanupDownloads();
        Directory.CreateDirectory(DownloadDir);
        var path = Path.Combine(DownloadDir, $"LabWidge-Setup-{release.Version}.exe");

        using (var response = await Downloader.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.Size;

            using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;
                progress?.Report((received, total));
            }
        }

        var actualSize = new FileInfo(path).Length;
        if (release.Size > 0 && actualSize != release.Size)
        {
            TryDelete(path);
            throw new InvalidOperationException(L.T("The downloaded file does not have the expected size.", "Den hentede fil har ikke den forventede størrelse."));
        }

        // Without a known SHA-256 the file cannot be verified – so it is not run either
        if (release.Sha256 == null)
        {
            TryDelete(path);
            throw new InvalidOperationException(L.T("The release states no SHA-256 checksum, so the file cannot be verified.", "Udgivelsen oplyser ingen SHA-256-sum, så filen kan ikke kontrolleres."));
        }

        var hash = await Sha256Async(path, token).ConfigureAwait(false);
        if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new InvalidOperationException(L.T("The SHA-256 checksum does not match the release.", "SHA-256-summen passer ikke med udgivelsen."));
        }
        Logger.Info("The update's SHA-256 is verified.");

        return path;
    }

    /// <summary>Starts the installer without a window. It closes the app, swaps the files and starts it again.</summary>
    public static void Launch(string setupPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = "--silent",
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(setupPath))!,
            UseShellExecute = false
        });
    }

    public static void OpenReleasesPage() =>
        Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });

    /// <summary>Cleans up previously downloaded installers.</summary>
    public static void CleanupDownloads()
    {
        try
        {
            if (!Directory.Exists(DownloadDir)) return;
            foreach (var file in Directory.GetFiles(DownloadDir)) TryDelete(file);
        }
        catch
        {
            // Cleanup is not critical.
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string? FindHash(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var match = HashNearLabel.Match(notes);
        if (!match.Success) match = AnyHash.Match(notes);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>The bullet points under "## &lt;version&gt;" in the bundled CHANGELOG.md.</summary>
    public static IReadOnlyList<string> BundledNotes(Version version)
    {
        var result = new List<string>();
        try
        {
            using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("CHANGELOG.md");
            if (stream == null) return result;
            using var reader = new StreamReader(stream);
            var inSection = false;
            while (reader.ReadLine() is string line)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (inSection) break;
                    inSection = ParseVersion(line[3..]) == version;
                }
                else if (inSection && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                {
                    result.Add(line.TrimStart()[2..].Trim());
                }
            }
        }
        catch
        {
            // If the text is missing, only the version number is shown.
        }
        return result;
    }

    /// <summary>Accepts "v1.4.0", "1.4.0" and "1.4.0+build".</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(clean, out var version) ? new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build)) : null;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignoreres */ }
    }
}

/// <summary>A small window that downloads the update and shows how far it has got.</summary>
internal sealed class UpdateDownloadForm : Form
{
    private static CultureInfo Da => L.Culture;

    private readonly ReleaseInfo _release;
    private readonly ProgressBar _progress = new() { Width = 420, Height = 8, Style = ProgressBarStyle.Continuous, Margin = new Padding(0, 14, 0, 6) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Ui.Muted };
    private readonly Button _cancel = new() { Text = L.T("Cancel", "Annuller"), Width = 96, Height = 30 };
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The path of the downloaded installer when the window closes with OK.</summary>
    public string? SetupPath { get; private set; }

    public UpdateDownloadForm(ReleaseInfo release)
    {
        _release = release;

        Text = L.T("LabWidge – Update", "LabWidge – Opdatering");
        Icon = AppIconProvider.GetIcon();
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        BackColor = Color.White;
        ClientSize = new Size(480, 190);

        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(24, 20, 20, 8)
        };
        body.Controls.Add(Ui.Heading(L.T($"Downloading version {release.Version}", $"Henter version {release.Version}")));
        body.Controls.Add(Ui.Help(L.T("The app closes and starts again by itself once the update is installed. Your settings are kept.",
                                      "Appen lukker og starter igen af sig selv, når opdateringen er installeret. Dine indstillinger bevares."), 420));
        body.Controls.Add(_progress);
        body.Controls.Add(_status);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = Color.FromArgb(246, 248, 250)
        };
        bottom.Controls.Add(_cancel);

        Controls.Add(body);
        Controls.Add(bottom);
        CancelButton = _cancel;

        _cancel.Click += (_, _) => _cts.Cancel();
        Shown += async (_, _) => await RunAsync();
        FormClosing += (_, _) => _cts.Cancel();
    }

    private async Task RunAsync()
    {
        var progress = new Progress<(long Received, long Total)>(p =>
        {
            if (p.Total > 0) _progress.Value = (int)Math.Min(100, p.Received * 100 / p.Total);
            _status.Text = p.Total > 0
                ? $"{Mb(p.Received)} " + L.T("of", "af") + $" {Mb(p.Total)} MB"
                : $"{Mb(p.Received)} MB";
        });

        try
        {
            SetupPath = await UpdateService.DownloadAsync(_release, progress, _cts.Token);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
        catch (Exception ex)
        {
            Logger.Error($"The update could not be downloaded: {ex.Message}");
            _progress.Visible = false;
            _status.Text = L.T("The update could not be downloaded: ", "Opdateringen kunne ikke hentes: ") + ex.Message;
            _status.ForeColor = Ui.Error;
            _cancel.Text = "Luk";
        }
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", Da);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>How long since the user last touched the mouse or keyboard.</summary>
internal static class UserIdle
{
    public static TimeSpan Duration
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            // Both are milliseconds since boot in 32 bits; uint subtraction handles the overflow after 49 days
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize, dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}
