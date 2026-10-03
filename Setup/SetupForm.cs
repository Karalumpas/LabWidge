using System;
using System.Diagnostics;
using System.Linq;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LabWidgeSetup
{
    internal sealed class SetupForm : Form
    {
        private static readonly Color Dark = Color.FromArgb(19, 23, 30);
        private static readonly Color Accent = Color.FromArgb(9, 105, 218);
        private static readonly Color Muted = Color.FromArgb(96, 104, 112);
        private static readonly Color Ok = Color.FromArgb(26, 127, 55);
        private static readonly Color Warn = Color.FromArgb(176, 100, 0);
        private static readonly Color Error = Color.FromArgb(207, 34, 46);

        private readonly Label _tagline = new Label { ForeColor = Color.FromArgb(145, 154, 164), AutoSize = true, Location = new Point(90, 54) };
        private readonly Label _languageLabel = new Label { AutoSize = true, Padding = new Padding(0, 6, 6, 0) };
        private readonly ComboBox _language = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly FlowLayoutPanel _languageRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
        private readonly Label _countryLabel = new Label { AutoSize = true, Padding = new Padding(0, 6, 6, 0) };
        private readonly ComboBox _country = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, MaxDropDownItems = 16 };
        private readonly FlowLayoutPanel _countryRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
        private string _countryCode = AppInstaller.CurrentCountry();
        private readonly Label _title = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 14F), Margin = new Padding(0, 0, 0, 6) };
        private readonly Label _intro = new Label { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Muted, Margin = new Padding(0, 0, 0, 14) };
        private readonly Label _runtimeIcon = StatusIcon();
        private readonly Label _runtimeText = StatusText();
        private readonly Label _appIcon = StatusIcon();
        private readonly Label _appText = StatusText();
        private readonly ProgressBar _progress = new ProgressBar { Width = 470, Height = 8, Visible = false, Margin = new Padding(0, 16, 0, 4), Style = ProgressBarStyle.Continuous };
        private readonly Label _status = new Label { AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = Muted };
        private readonly LinkLabel _manualLink = new LinkLabel { AutoSize = true, Visible = false, Margin = new Padding(0, 6, 0, 0) };
        private readonly CheckBox _startApp = new CheckBox { AutoSize = true, Checked = true, Visible = false, Margin = new Padding(0, 14, 0, 0) };
        private readonly Button _primary = new Button { Width = 120, Height = 32, BackColor = Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        private readonly Button _cancel = new Button { Width = 96, Height = 32 };

        private CancellationTokenSource _cts;
        private Version _runtime;
        private bool _done;
        private bool _busy;

        public SetupForm()
        {
            Font = new Font("Segoe UI", 9.5F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 510);
            BackColor = Color.White;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            // Top banner
            var banner = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Dark };
            var logo = new PictureBox { Size = new Size(52, 52), Location = new Point(24, 20), SizeMode = PictureBoxSizeMode.Zoom };
            using (var ico = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
            {
                if (ico != null) logo.Image = new Icon(ico, 256, 256).ToBitmap();
            }
            banner.Controls.Add(logo);
            banner.Controls.Add(new Label { Text = "LabWidge", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 15F), AutoSize = true, Location = new Point(88, 22) });
            banner.Controls.Add(_tagline);

            // The language is chosen first; every text below follows it at once
            _language.Items.AddRange(new object[] { "English", "Dansk" });
            _language.SelectedIndex = L.IsDanish ? 1 : 0;
            _language.SelectedIndexChanged += (_, __) =>
            {
                L.Use(_language.SelectedIndex == 1 ? L.Danish : L.English);
                ApplyTexts();
            };
            _languageRow.Controls.Add(_languageLabel);
            _languageRow.Controls.Add(_language);

            // The country decides which electricity prices the app shows; "Other country" hides them
            _country.SelectedIndexChanged += (_, __) => _countryCode = (_country.SelectedItem as Country)?.Code ?? Countries.Other;
            _countryRow.Controls.Add(_countryLabel);
            _countryRow.Controls.Add(_country);

            // Content
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(28, 16, 24, 8) };
            body.Controls.Add(_languageRow);
            body.Controls.Add(_countryRow);
            body.Controls.Add(_title);
            body.Controls.Add(_intro);
            body.Controls.Add(Row(_runtimeIcon, _runtimeText));
            body.Controls.Add(Row(_appIcon, _appText));
            body.Controls.Add(_progress);
            body.Controls.Add(_status);
            body.Controls.Add(_manualLink);
            body.Controls.Add(_startApp);

            // Buttons
            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Height = 56,
                Padding = new Padding(16, 11, 16, 10),
                BackColor = Color.FromArgb(246, 248, 250)
            };
            _primary.FlatAppearance.BorderSize = 0;
            bottom.Controls.Add(_primary);
            bottom.Controls.Add(_cancel);

            Controls.Add(body);
            Controls.Add(bottom);
            Controls.Add(banner);
            AcceptButton = _primary;

            _primary.Click += async (_, __) => await PrimaryAsync();
            _cancel.Click += (_, __) => CancelOrClose();
            _manualLink.LinkClicked += (_, __) => Process.Start(new ProcessStartInfo(RuntimeInstaller.DownloadPage) { UseShellExecute = true });
            FormClosing += (_, e) =>
            {
                if (_busy && MessageBox.Show(this, L.T("The installation is in progress. Cancel it?", "Installationen er i gang. Vil du afbryde?"),
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cts?.Cancel();
            };

            _runtime = RuntimeInstaller.FindDesktopRuntime();
            ApplyTexts();
        }

        private static Label StatusIcon() => new Label { AutoSize = false, Size = new Size(22, 24), Font = new Font("Segoe UI Semibold", 10.5F), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) };
        private static Label StatusText() => new Label { AutoSize = true, MaximumSize = new Size(440, 0), Padding = new Padding(0, 3, 0, 0), Margin = new Padding(0) };

        private static Control Row(Label icon, Label text)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 6) };
            row.Controls.Add(icon);
            row.Controls.Add(text);
            return row;
        }

        private static void SetStatus(Label icon, Label text, string glyph, Color color, string message)
        {
            icon.Text = glyph;
            icon.ForeColor = color;
            text.Text = message;
        }

        /// <summary>All texts of the start screen, in the chosen language. Only used before the installation starts.</summary>
        private void ApplyTexts()
        {
            var installed = AppInstaller.InstalledVersion;
            Text = L.T("LabWidge – Installation", "LabWidge – Installation");
            _tagline.Text = L.T($"Version {AppInstaller.PackagedVersion}  ·  Power price, system and homelab by the clock",
                                $"Version {AppInstaller.PackagedVersion}  ·  Elpris, system og hjemmelab ved uret");
            _languageLabel.Text = L.T("Language", "Sprog");
            _countryLabel.Text = L.T("Country", "Land");
            FillCountries();
            _manualLink.Text = L.T("Download .NET 8 Desktop Runtime manually from Microsoft", "Hent .NET 8 Desktop Runtime manuelt hos Microsoft");
            _startApp.Text = L.T("Start LabWidge", "Start LabWidge");
            _cancel.Text = L.T("Cancel", "Annuller");

            _title.Text = installed == null ? L.T("Install LabWidge", "Installér LabWidge") : L.T("Update LabWidge", "Opdatér LabWidge");
            _intro.Text = L.T($"LabWidge is installed for your user only, in {AppInstaller.InstallDir}. " +
                              "The app uses Microsoft .NET 8 Desktop Runtime, which is installed from Microsoft if it is missing.",
                              $"LabWidge installeres kun for din bruger i {AppInstaller.InstallDir}. " +
                              "Appen bruger Microsoft .NET 8 Desktop Runtime, som installeres fra Microsoft hvis den mangler.");

            if (_runtime != null)
                SetStatus(_runtimeIcon, _runtimeText, "✓", Ok, L.T($"Microsoft .NET Desktop Runtime {_runtime} is installed", $"Microsoft .NET Desktop Runtime {_runtime} er installeret"));
            else
                SetStatus(_runtimeIcon, _runtimeText, "!", Warn, L.T("Microsoft .NET 8 Desktop Runtime is missing – downloaded from Microsoft (about 56 MB, needs administrator)",
                                                                     "Microsoft .NET 8 Desktop Runtime mangler – hentes fra Microsoft (ca. 56 MB, kræver administrator)"));

            SetStatus(_appIcon, _appText, "•", Muted, installed == null
                ? $"LabWidge {AppInstaller.PackagedVersion}"
                : L.T($"LabWidge {installed} is updated to {AppInstaller.PackagedVersion}", $"LabWidge {installed} opdateres til {AppInstaller.PackagedVersion}"));

            _primary.Text = installed == null ? L.T("Install", "Installér") : L.T("Update", "Opdatér");
        }

        /// <summary>The countries in the chosen language, sorted by name, with "Other country" last.</summary>
        private void FillCountries()
        {
            var code = _countryCode;
            _country.BeginUpdate();
            _country.Items.Clear();
            foreach (var c in Countries.All.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)) _country.Items.Add(c);
            var other = L.T("Other country (no electricity price)", "Andet land (ingen elpris)");
            _country.Items.Add(other);
            _country.EndUpdate();
            _country.SelectedItem = (object)Countries.Find(code) ?? other;
        }

        private void CancelOrClose()
        {
            if (_busy) _cts?.Cancel();
            else Close();
        }

        private async Task PrimaryAsync()
        {
            if (_done)
            {
                if (_startApp.Checked) AppInstaller.Launch(runSetupGuide: false);
                Close();
                return;
            }

            _busy = true;
            _primary.Enabled = false;
            _language.Enabled = false;
            _country.Enabled = false;
            _manualLink.Visible = false;
            _cts = new CancellationTokenSource();
            try
            {
                if (_runtime == null && !await EnsureRuntimeAsync(_cts.Token)) return;

                SetStatus(_appIcon, _appText, "…", Accent, L.T($"Installing LabWidge {AppInstaller.PackagedVersion}…", $"Installerer LabWidge {AppInstaller.PackagedVersion}…"));
                _status.Text = "";
                await Task.Run(() => AppInstaller.Install());
                // The app takes the language over into its settings when it starts
                L.RememberInstallerChoice(L.Current);
                Countries.RememberInstallerChoice(_countryCode);
                SetStatus(_appIcon, _appText, "✓", Ok, L.T($"LabWidge {AppInstaller.PackagedVersion} is installed", $"LabWidge {AppInstaller.PackagedVersion} er installeret"));

                _languageRow.Visible = false;
                _countryRow.Visible = false;
                _title.Text = L.T("LabWidge is ready", "LabWidge er klar");
                _intro.Text = AppInstaller.SettingsExist
                    ? L.T("Your settings are kept. Click the bolt by the clock to show the widget.", "Dine indstillinger er bevaret. Klik på lynet ved uret for at vise widgetten.")
                    : L.T("When the app starts, the setup guide helps you with electricity price, notifications and appearance.",
                          "Når appen starter, hjælper opsætningsguiden dig med elpris, notifikationer og udseende.");
                _progress.Visible = false;
                _startApp.Visible = true;
                _primary.Text = L.T("Close", "Luk");
                _cancel.Visible = false;
                _done = true;
            }
            catch (OperationCanceledException)
            {
                _progress.Visible = false;
                _status.Text = L.T("The installation was cancelled.", "Installationen blev afbrudt.");
                _status.ForeColor = Warn;
            }
            catch (Exception ex)
            {
                _progress.Visible = false;
                _status.Text = L.T("The installation failed: ", "Installationen fejlede: ") + ex.Message;
                _status.ForeColor = Error;
            }
            finally
            {
                _busy = false;
                _primary.Enabled = true;
                if (!_done) _language.Enabled = _country.Enabled = true;
            }
        }

        private async Task<bool> EnsureRuntimeAsync(CancellationToken token)
        {
            var answer = MessageBox.Show(this,
                L.T("LabWidge requires Microsoft .NET 8 Desktop Runtime, which is not installed.\n\n" +
                    "May the installer download it from Microsoft (about 56 MB) and install it now?\n\n" +
                    "Windows asks for administrator rights for the runtime installation.",
                    "LabWidge kræver Microsoft .NET 8 Desktop Runtime, som ikke er installeret.\n\n" +
                    "Må installationen hente den fra Microsoft (ca. 56 MB) og installere den nu?\n\n" +
                    "Windows beder om administratorrettigheder til runtime-installationen."),
                "Microsoft .NET 8 Desktop Runtime", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
            {
                SetStatus(_runtimeIcon, _runtimeText, "✗", Error, L.T("Microsoft .NET 8 Desktop Runtime is not installed", "Microsoft .NET 8 Desktop Runtime er ikke installeret"));
                _status.Text = L.T("LabWidge cannot run without the runtime. You can install it yourself and then run the installer again.",
                                   "LabWidge kan ikke køre uden runtimen. Du kan installere den selv og derefter køre installationen igen.");
                _status.ForeColor = Warn;
                _manualLink.Visible = true;
                return false;
            }

            _progress.Visible = true;
            _progress.Value = 0;
            _status.ForeColor = Muted;
            SetStatus(_runtimeIcon, _runtimeText, "…", Accent, L.T("Downloading Microsoft .NET 8 Desktop Runtime…", "Henter Microsoft .NET 8 Desktop Runtime…"));

            string installer;
            try
            {
                var progress = new Progress<(long Received, long Total)>(p =>
                {
                    if (p.Total <= 0) return;
                    _progress.Value = (int)Math.Min(100, p.Received * 100 / p.Total);
                    _status.Text = (p.Received / 1048576.0).ToString("0.0", L.Culture) + L.T(" of ", " af ")
                                   + (p.Total / 1048576.0).ToString("0.0", L.Culture) + " MB";
                });
                installer = await RuntimeInstaller.DownloadAsync(progress, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetStatus(_runtimeIcon, _runtimeText, "✗", Error, L.T("The runtime could not be downloaded", "Runtimen kunne ikke hentes"));
                _status.Text = ex.Message;
                _status.ForeColor = Error;
                _manualLink.Visible = true;
                _progress.Visible = false;
                return false;
            }

            SetStatus(_runtimeIcon, _runtimeText, "…", Accent, L.T("Installing Microsoft .NET 8 Desktop Runtime – approve it in the Windows dialog…",
                                                                   "Installerer Microsoft .NET 8 Desktop Runtime – godkend i Windows-dialogen…"));
            _progress.Style = ProgressBarStyle.Marquee;
            _status.Text = L.T("The Microsoft signature is verified.", "Signatur fra Microsoft er bekræftet.");

            bool installedOk;
            try
            {
                installedOk = await RuntimeInstaller.InstallAsync(installer);
            }
            catch (OperationCanceledException)
            {
                _progress.Style = ProgressBarStyle.Continuous;
                SetStatus(_runtimeIcon, _runtimeText, "✗", Error, L.T("The runtime was not installed", "Runtimen blev ikke installeret"));
                _status.Text = L.T("Administrator rights were refused. Try again, or install the runtime manually.",
                                   "Administratorrettigheder blev afvist. Prøv igen, eller installér runtimen manuelt.");
                _status.ForeColor = Warn;
                _manualLink.Visible = true;
                _progress.Visible = false;
                return false;
            }
            finally
            {
                try { System.IO.File.Delete(installer); } catch { /* ignored */ }
            }

            _progress.Style = ProgressBarStyle.Continuous;
            _runtime = RuntimeInstaller.FindDesktopRuntime();
            if (!installedOk || _runtime == null)
            {
                SetStatus(_runtimeIcon, _runtimeText, "✗", Error, L.T("The runtime installation failed", "Installationen af runtimen fejlede"));
                _status.Text = L.T("Try again, or install the runtime manually.", "Prøv igen, eller installér runtimen manuelt.");
                _status.ForeColor = Error;
                _manualLink.Visible = true;
                _progress.Visible = false;
                return false;
            }

            SetStatus(_runtimeIcon, _runtimeText, "✓", Ok, L.T($"Microsoft .NET Desktop Runtime {_runtime} is installed", $"Microsoft .NET Desktop Runtime {_runtime} er installeret"));
            _progress.Visible = false;
            _status.Text = "";
            return true;
        }
    }
}
