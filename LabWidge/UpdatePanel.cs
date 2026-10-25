using System.Diagnostics;
using System.Windows.Forms;

/// <summary>
/// The update section on About: which version runs and where it comes from, a check, and – when a newer version exists – one
/// button that downloads and installs it. The Store version asks Microsoft Store; the GitHub version uses the verified installer.
/// </summary>
internal sealed class UpdatePanel : FlowLayoutPanel, IIgnoredByEditState
{
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(Ui.ContentWidth, 0), Margin = new Padding(0, 2, 0, 6) };
    private readonly Button _check = new() { AutoSize = true, Text = L.T("Check for updates", "Søg efter opdateringer") };
    private readonly Button _install = new() { AutoSize = true, Tag = "accent", Visible = false };
    private readonly LinkLabel _storePage = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0), Text = L.T("Open LabWidge in Microsoft Store", "Åbn LabWidge i Microsoft Store") };
    private readonly CancellationTokenSource _lifetime = new();
    private IUpdateSource? _source;
    private UpdateOffer? _offer;
    private bool _busy;

    internal UpdateChannel Channel => (_source ??= AppUpdates.Factory()).Channel;
    internal string StatusText => _status.Text;
    internal UpdateOffer? Offer => _offer;

    public UpdatePanel()
    {
        AutoSize = true;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        Margin = Padding.Empty;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        _install.Margin = new Padding(8, 0, 0, 0);
        buttons.Controls.AddRange(new Control[] { _check, _install });
        Controls.AddRange(new Control[] { _status, buttons, _storePage });
        _storePage.Visible = Channel == UpdateChannel.Store;
        _status.Text = Running();
        _status.ForeColor = Ui.Muted;
        _check.Click += async (_, _) => await CheckAsync();
        _install.Click += async (_, _) => await InstallAsync();
        _storePage.LinkClicked += (_, _) => Open(AppUpdates.StorePage);
        Disposed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private string Running() => Channel == UpdateChannel.Store
        ? L.T($"You are running version {UpdateService.Current} from Microsoft Store, which installs updates by itself – or check now.",
              $"Du kører version {UpdateService.Current} fra Microsoft Store, som installerer opdateringer af sig selv – eller søg nu.")
        : L.T($"You are running version {UpdateService.Current}, installed from GitHub.",
              $"Du kører version {UpdateService.Current}, installeret fra GitHub.");

    private void Show(string text, System.Drawing.Color color)
    {
        // Progress arrives asynchronously – possibly on another thread, or after the window closed
        if (IsDisposed || Disposing || _status.IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => Show(text, color)); } catch (InvalidOperationException) { }
            return;
        }
        if (FindForm() is not { IsDisposed: false, Disposing: false }) return;
        _status.Text = text;
        _status.ForeColor = color;
    }

    /// <summary>Asks the source for a newer version. Also started by "Check for updates" in the tray menu.</summary>
    internal async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        _check.Enabled = false;
        _install.Visible = false;
        _offer = null;
        Show(Channel == UpdateChannel.Store ? L.T("Asking Microsoft Store…", "Spørger Microsoft Store…") : L.T("Asking GitHub…", "Spørger GitHub…"), Ui.Muted);
        try
        {
            _offer = await _source!.CheckAsync(_lifetime.Token);
            if (IsDisposed) return;
            if (_offer == null)
            {
                Show(L.T($"✓ You are running the latest version ({UpdateService.Current}).", $"✓ Du kører den nyeste version ({UpdateService.Current})."), Ui.Ok);
                return;
            }
            var size = _offer.Size > 0 ? $" ({_offer.Size / 1048576.0:0.#} MB)" : "";
            Show(L.T($"Version {_offer.Version} is ready – you are running {UpdateService.Current}.", $"Version {_offer.Version} er klar – du kører {UpdateService.Current}.")
                 + (_offer.Notes.Trim().Length > 0 ? "\n" + Shorten(_offer.Notes.Trim(), 300) : ""), Ui.Ok);
            _install.Text = L.T($"Download and install {_offer.Version}{size}", $"Hent og installér {_offer.Version}{size}");
            _install.Visible = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Error($"Update check ({Channel}) failed: {ex.Message}");
            Show((Channel == UpdateChannel.Store ? L.T("Could not reach Microsoft Store: ", "Kunne ikke kontakte Microsoft Store: ")
                                                  : L.T("Could not reach GitHub: ", "Kunne ikke kontakte GitHub: ")) + ex.Message, Ui.Error);
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) _check.Enabled = true;
        }
    }

    private async Task InstallAsync()
    {
        if (_busy || _offer is not { } offer) return;
        _busy = true;
        _check.Enabled = _install.Enabled = false;
        var downloading = true;
        var progress = new Progress<double>(p =>
        {
            if (downloading) Show(L.T($"Downloading {offer.Version}… {p * 100:0} %", $"Henter {offer.Version}… {p * 100:0} %"), Ui.Muted);
        });
        Show(L.T($"Downloading {offer.Version}…", $"Henter {offer.Version}…"), Ui.Muted);
        try
        {
            var form = FindForm()!;
            var accepted = await _source!.InstallAsync(offer, form, progress, _lifetime.Token);
            downloading = false;
            if (!accepted)
            {
                Show(L.T("The update was cancelled.", "Opdateringen blev annulleret."), Ui.Muted);
                return;
            }
            Logger.Info($"Update to {offer.Version} ({Channel}) accepted.");
            if (Channel == UpdateChannel.Store)
            {
                Show(L.T("✓ Microsoft Store is installing the update. LabWidge closes and starts again by itself.",
                         "✓ Microsoft Store installerer opdateringen. LabWidge lukker og starter igen af sig selv."), Ui.Ok);
            }
            else
            {
                // The verified installer runs once Settings has closed; it closes LabWidge and starts the new version
                Show(L.T("✓ Downloaded and verified. LabWidge closes and installs the update now.",
                         "✓ Hentet og kontrolleret. LabWidge lukker og installerer opdateringen nu."), Ui.Ok);
                form.Close();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Error($"Update to {offer.Version} ({Channel}) failed: {ex.Message}");
            Show(L.T("The update could not be installed: ", "Opdateringen kunne ikke installeres: ") + ex.Message, Ui.Error);
        }
        finally
        {
            downloading = false;
            _busy = false;
            if (!IsDisposed) _check.Enabled = _install.Enabled = true;
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Error($"Could not open {url}: {ex.Message}"); }
    }
}
