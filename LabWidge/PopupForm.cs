using System.Drawing;
using System.Windows.Forms;

/// <summary>The "IP &amp; DNS records" window from the tray menu: the external IP, Cloudflare actions and the DNS records.</summary>
internal sealed class PopupForm : Form
{
    private static PopupForm? _instance;
    private static Label? _statusLabel;
    private bool _suppressDeactivateClose;
    private readonly AppSettings _settings;
    private readonly DataGridView _dnsGrid;
    /// <summary>The widget's colours, so the window follows the light or dark theme like the rest of LabWidge.</summary>
    private readonly Palette _p;

    private PopupForm(string ip, string timestamp, AppSettings settings, Action onCopy, Func<Task> onUpdate, Action onCloudflare, Action onSettings, Func<Task> onRefreshCloudflare)
    {
        _settings = settings;
        _p = Palette.For(settings);
        var card = SettingsTheme.CardColor(_p);
        var page = SettingsTheme.PageColor(_p);
        Text = "LabWidge";
        Icon = AppIconProvider.GetIcon();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = page;
        ForeColor = _p.TextPrimary;
        ClientSize = new Size(500, 380);

        // Two pages chosen by buttons: a TabControl draws its tab strip in the system's light colours whatever the theme
        var overview = new Panel { Dock = DockStyle.Fill, BackColor = card };
        var dns = new Panel { Dock = DockStyle.Fill, BackColor = card, Visible = false };
        var tabStrip = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, BackColor = page, Padding = new Padding(8, 6, 8, 0), WrapContents = false };
        var overviewTab = TabButton(L.T("Overview", "Oversigt"));
        var dnsTab = TabButton(L.T("DNS records", "DNS-records"));
        tabStrip.Controls.AddRange(new Control[] { overviewTab, dnsTab });

        // --- Overview ---
        var headerPanel = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = _p.IsDark ? Color.FromArgb(30, 40, 52) : Color.FromArgb(236, 240, 245) };
        var ipTitle = new Label
        {
            Text = L.T("YOUR IP ADDRESS", "DIN IP-ADRESSE") + $"  (v{Application.ProductVersion.Split('+')[0]})",
            ForeColor = _p.TextSecondary,
            Location = new Point(20, 15),
            AutoSize = true,
            Font = new Font("Segoe UI", 8F, FontStyle.Bold)
        };
        var ipDisplay = new Label
        {
            Text = ip,
            ForeColor = _p.TextPrimary,
            Location = new Point(18, 35),
            AutoSize = true,
            Font = new Font("Segoe UI", 20F, FontStyle.Bold)
        };
        headerPanel.Controls.AddRange(new Control[] { ipTitle, ipDisplay });

        var timeLabel = new Label
        {
            Text = L.T("Last updated: ", "Sidst opdateret: ") + timestamp,
            AutoSize = true,
            ForeColor = _p.TextSecondary,
            Location = new Point(20, 100)
        };

        var btnCopy = CreateButton(L.T("Copy IP", "Kopiér IP"), 20, 140, onCopy);
        var btnUpdate = CreateButtonAsync(L.T("Refresh IP now", "Opdatér IP nu"), 130, 140, onUpdate);
        var btnSettings = CreateButton(L.T("Settings", "Indstillinger"), 240, 140, () =>
        {
            _suppressDeactivateClose = true;
            Hide();
            onSettings();
            Close();
        });

        var btnCf = CreateButton(L.T("Open Cloudflare", "Åbn Cloudflare"), 20, 180, onCloudflare);
        btnCf.Width = 210; // the name did not fit in a normal button
        var btnUpdateCf = CreateButtonAsync(L.T("Refresh status", "Opfrisk status"), 240, 180, onRefreshCloudflare);

        _statusLabel = new Label
        {
            Text = L.T("Ready.", "Klar."),
            AutoSize = false,
            Dock = DockStyle.Bottom,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = _p.TextSecondary,
            Padding = new Padding(10, 0, 0, 0),
            BackColor = page
        };

        overview.Controls.AddRange(new Control[] { headerPanel, timeLabel, btnCopy, btnUpdate, btnSettings, btnCf, btnUpdateCf, _statusLabel });

        // --- Cloudflare DNS ---
        var dnsHeader = new Panel { Dock = DockStyle.Top, Height = 45, BackColor = card };
        var btnRefresh = CreateButton(L.T("Refresh", "Opfrisk"), 10, 8, async () => await LoadDnsRecords());
        btnRefresh.Width = 100;
        dnsHeader.Controls.Add(btnRefresh);

        _dnsGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            RowHeadersVisible = false,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        _dnsGrid.Columns.Add("Type", "Type");
        _dnsGrid.Columns["Type"]!.Width = 60;
        _dnsGrid.Columns.Add("Name", "Host");
        _dnsGrid.Columns.Add("Content", L.T("Content", "Indhold"));
        _dnsGrid.Columns.Add("Proxied", "Proxy");

        // Docking follows reverse z-order: the header is added last so it takes the top strip and the grid fills the rest
        dns.Controls.Add(_dnsGrid);
        dns.Controls.Add(dnsHeader);
        SettingsTheme.Apply(dns, _p); // the grid gets the settings' grid colours
        _dnsGrid.BorderStyle = BorderStyle.None;

        void Select(bool showDns)
        {
            overview.Visible = !showDns;
            dns.Visible = showDns;
            MarkTab(overviewTab, !showDns);
            MarkTab(dnsTab, showDns);
        }
        overviewTab.Click += (_, _) => Select(false);
        dnsTab.Click += async (_, _) =>
        {
            if (dns.Visible) return;
            Select(true);
            await LoadDnsRecords(); // the records are fetched when the page is opened
        };
        Select(false);

        Controls.Add(overview);
        Controls.Add(dns);
        Controls.Add(tabStrip);

        HandleCreated += (_, _) => SettingsTheme.TitleBar(this, _p);
        Deactivate += (_, _) =>
        {
            if (_suppressDeactivateClose) return;
            Close();
        };
    }

    private Button TabButton(string text)
    {
        var tab = new Button
        {
            Text = text, AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 4, 0), Padding = new Padding(8, 0, 8, 0), Cursor = Cursors.Hand
        };
        tab.FlatAppearance.BorderSize = 0;
        tab.FlatAppearance.MouseOverBackColor = _p.HoverBg;
        return tab;
    }

    private void MarkTab(Button tab, bool selected)
    {
        tab.BackColor = selected ? SettingsTheme.CardColor(_p) : SettingsTheme.PageColor(_p);
        tab.ForeColor = selected ? _p.TextPrimary : _p.TextSecondary;
        tab.Font = new Font(Font, selected ? FontStyle.Bold : FontStyle.Regular);
    }

    private Button CreateButton(string text, int x, int y, Action onClick)
    {
        var btn = ThemedButton(text, x, y);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Button CreateButtonAsync(string text, int x, int y, Func<Task> onClick)
    {
        var btn = ThemedButton(text, x, y);
        btn.Click += async (_, _) =>
        {
            btn.Enabled = false;
            try { await onClick(); }
            finally { btn.Enabled = true; }
        };
        return btn;
    }

    private Button ThemedButton(string text, int x, int y)
    {
        var btn = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(100, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = _p.IsDark ? Color.FromArgb(38, 45, 56) : Color.FromArgb(246, 248, 250),
            ForeColor = _p.TextPrimary,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = _p.Line;
        btn.FlatAppearance.MouseOverBackColor = _p.HoverBg;
        return btn;
    }

    public static void ShowPopup(string ip, string timestamp, AppSettings settings, Action onCopy, Func<Task> onUpdate, Action onCloudflare, Action onSettings, Func<Task> onRefreshCloudflare)
    {
        _instance?.Close();
        _instance = new PopupForm(ip, timestamp, settings, onCopy, onUpdate, onCloudflare, onSettings, onRefreshCloudflare);

        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor);
        var x = Math.Min(cursor.X, screen.WorkingArea.Right - _instance.Width - 10);
        var y = Math.Min(cursor.Y, screen.WorkingArea.Bottom - _instance.Height - 10);
        _instance.Location = new Point(Math.Max(screen.WorkingArea.Left + 10, x),
                                       Math.Max(screen.WorkingArea.Top + 10, y));

        _instance.Show();
        _instance.Activate();
    }

    private async Task LoadDnsRecords()
    {
        _dnsGrid.Rows.Clear();
        var token = CredentialStore.ReadToken();
        if (string.IsNullOrWhiteSpace(_settings.ZoneId) || string.IsNullOrWhiteSpace(token))
        {
            _dnsGrid.Rows.Add(L.T("Not set up", "Mangler opsætning"), "", "", "");
            return;
        }

        try
        {
            var records = await CloudflareClient.GetRecordsAsync(_settings.ZoneId, token, "A,CNAME");
            foreach (var r in records)
            {
                _dnsGrid.Rows.Add(r.Type, r.Name, r.Content, (r.Proxied ?? false) ? "Proxied" : "DNS Only");
            }
        }
        catch (Exception ex)
        {
            _dnsGrid.Rows.Add(L.T("Error: ", "Fejl: ") + ex.Message, "", "", "");
        }
    }

    public static void SetStatus(string message, StatusLevel level = StatusLevel.Info)
    {
        if (_statusLabel == null || _instance == null || _instance.IsDisposed)
        {
            return;
        }

        if (_instance.InvokeRequired)
        {
            _instance.BeginInvoke(new Action(() => SetStatus(message, level)));
            return;
        }

        var p = _instance._p;
        _statusLabel.Text = message;
        _statusLabel.ForeColor = level switch
        {
            StatusLevel.Success => p.Green,
            StatusLevel.Warning => p.Amber,
            StatusLevel.Error => p.Red,
            _ => p.TextSecondary
        };
    }
}
