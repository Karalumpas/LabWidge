using System.Drawing;
using System.Windows.Forms;

/// <summary>The settings window with tabs.</summary>
internal sealed class SettingsWindow : Form
{
    private readonly SettingsPage[] _pages = { new WidgetPage(), new PricePage(), new AudioPage(), new HomeAssistantPage(), new CloudflarePage(), new ProxmoxPage(), new NotificationsPage() };
    private readonly HashSet<SettingsPage> _shown = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 6) };

    public AppSettings Settings { get; }
    public bool RunOnboardingRequested { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        Settings = settings.Clone();

        Text = L.T("LabWidge – Settings", "LabWidge – Indstillinger");
        Icon = AppIconProvider.GetIcon();
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5F);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(640, 620);
        MinimumSize = new Size(560, 480);

        foreach (var page in _pages)
        {
            page.LoadFrom(Settings);
            var tab = new TabPage(page.Title) { BackColor = Color.White };
            tab.Controls.Add(page);
            _tabs.TabPages.Add(tab);
        }
        _tabs.SelectedIndexChanged += async (_, _) => await ShowPageAsync(_tabs.SelectedIndex);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = Color.FromArgb(246, 248, 250)
        };
        var save = new Button { Text = L.T("Save", "Gem"), Width = 96, Height = 30, BackColor = Ui.Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        save.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = L.T("Cancel", "Annuller"), Width = 96, Height = 30, DialogResult = DialogResult.Cancel };
        var wizard = new LinkLabel { Text = L.T("Run the setup guide again", "Kør opsætningsguiden igen"), AutoSize = true, Margin = new Padding(0, 8, 180, 0) };
        buttons.Controls.AddRange(new Control[] { save, cancel, wizard });

        save.Click += (_, _) => SaveAndClose();
        wizard.LinkClicked += (_, _) =>
        {
            RunOnboardingRequested = true;
            DialogResult = DialogResult.Abort;
            Close();
        };

        Controls.Add(_tabs);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;

        Shown += async (_, _) => await ShowPageAsync(0);
    }

    private async Task ShowPageAsync(int index)
    {
        if (index < 0 || index >= _pages.Length) return;
        var page = _pages[index];
        if (_shown.Add(page)) await page.OnFirstShownAsync();
    }

    private void SaveAndClose()
    {
        for (var i = 0; i < _pages.Length; i++)
        {
            var error = _pages[i].SaveTo(Settings);
            if (error != null)
            {
                _tabs.SelectedIndex = i;
                MessageBox.Show(this, error, L.T("Settings", "Indstillinger"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Setup guide for new users.</summary>
internal sealed class OnboardingForm : Form
{
    private readonly SettingsPage[] _pages = { new WelcomePage(), new WidgetPage(), new PricePage(), new HomeAssistantPage(), new CloudflarePage(), new NotificationsPage() };
    private readonly string[] _stepNames =
    {
        L.T("Welcome", "Velkommen"), "Widget", L.T("Electricity price", "Elpris"), "Home Assistant", "Cloudflare", L.T("Notifications", "Notifikationer")
    };
    private readonly HashSet<SettingsPage> _shown = new();
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly Label[] _stepLabels;
    private readonly Button _back = new() { Text = L.T("Back", "Tilbage"), Width = 96, Height = 32 };
    private readonly Button _next = new() { Text = L.T("Next", "Næste"), Width = 110, Height = 32, BackColor = Ui.Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Label _progress = new() { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 9, 0, 0) };
    private int _index;

    public AppSettings Settings { get; }

    public OnboardingForm(AppSettings settings)
    {
        Settings = settings.Clone();

        Text = L.T("LabWidge – Setup", "LabWidge – Opsætning");
        Icon = AppIconProvider.GetIcon();
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5F);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(780, 600);

        // Sidebar with logo and steps
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = Color.FromArgb(19, 23, 30), Padding = new Padding(20, 24, 12, 16) };
        var logo = new PictureBox { Image = WidgetIcon.Render(96, WidgetIcon.Amber), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(44, 44), Location = new Point(20, 24) };
        var appName = new Label { Text = "LabWidge", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 12F), AutoSize = true, Location = new Point(20, 76) };
        var tagline = new Label { Text = L.T("Power price · system · homelab", "Elpris · system · hjemmelab"), ForeColor = Color.FromArgb(145, 154, 164), AutoSize = true, Location = new Point(20, 100) };
        sidebar.Controls.AddRange(new Control[] { logo, appName, tagline });

        _stepLabels = _stepNames.Select((name, i) => new Label
        {
            Text = $"{i + 1}   {name}",
            AutoSize = false,
            Size = new Size(168, 30),
            Location = new Point(12, 150 + i * 34),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            ForeColor = Color.FromArgb(145, 154, 164)
        }).ToArray();
        sidebar.Controls.AddRange(_stepLabels);

        // Bottom bar
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(246, 248, 250) };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(0, 11, 16, 10),
            BackColor = Color.Transparent
        };
        _next.FlatAppearance.BorderSize = 0;
        buttons.Controls.AddRange(new Control[] { _next, _back });
        _progress.AutoSize = false;
        _progress.Dock = DockStyle.Fill;
        _progress.TextAlign = ContentAlignment.MiddleLeft;
        _progress.Padding = new Padding(16, 0, 0, 0);
        bottom.Controls.Add(_progress);
        bottom.Controls.Add(buttons);

        _back.Click += async (_, _) => await GoToAsync(_index - 1);
        _next.Click += async (_, _) => await NextAsync();

        foreach (var page in _pages) page.LoadFrom(Settings);

        Controls.Add(_host);
        Controls.Add(bottom);
        Controls.Add(sidebar);
        AcceptButton = _next;

        Shown += async (_, _) => await GoToAsync(0);
    }

    private async Task NextAsync()
    {
        var error = _pages[_index].SaveTo(Settings);
        if (error != null)
        {
            MessageBox.Show(this, error, L.T("Setup", "Opsætning"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_index == _pages.Length - 1)
        {
            Settings.OnboardingCompleted = true;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        await GoToAsync(_index + 1);
    }

    private async Task GoToAsync(int index)
    {
        if (index < 0 || index >= _pages.Length) return;
        _index = index;

        _host.SuspendLayout();
        _host.Controls.Clear();
        _host.Controls.Add(_pages[index]);
        _host.ResumeLayout();

        for (var i = 0; i < _stepLabels.Length; i++)
        {
            var active = i == index;
            _stepLabels[i].BackColor = active ? Color.FromArgb(40, 48, 60) : Color.Transparent;
            _stepLabels[i].ForeColor = active ? Color.White : i < index ? Color.FromArgb(63, 185, 80) : Color.FromArgb(145, 154, 164);
            _stepLabels[i].Text = (i < index ? "✓" : $"{i + 1}") + "   " + _stepNames[i];
        }

        _back.Visible = index > 0;
        _next.Text = index == _pages.Length - 1 ? L.T("Get started", "Kom i gang") : L.T("Next", "Næste");
        _progress.Text = L.T($"Step {index + 1} of {_pages.Length}", $"Trin {index + 1} af {_pages.Length}");

        if (_shown.Add(_pages[index])) await _pages[index].OnFirstShownAsync();
    }
}
