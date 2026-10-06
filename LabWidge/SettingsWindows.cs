using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

/// <summary>
/// The settings: a menu of pages on the left, grouped in general, data and home lab, and the chosen page on the right –
/// drawn in the widget's light or dark theme.
/// </summary>
internal sealed class SettingsWindow : Form
{
    private SettingsPage[] _pages = Array.Empty<SettingsPage>();
    private readonly Dictionary<string, SettingsPage> _pluginPages = new();
    private readonly SettingsPage[] _commonPages;
    private readonly PluginsPage _pluginsPage;
    private readonly AboutPage _aboutPage = new();
    private readonly HashSet<SettingsPage> _shown = new();
    private readonly Panel _host = new() { Dock = DockStyle.Fill };
    private readonly NavMenu _menu;
    private readonly Palette _p;
    private int _index = -1;

    public AppSettings Settings { get; }
    public bool RunOnboardingRequested { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        Settings = settings.Clone();
        _p = Palette.For(settings.Theme);
        Ui.Palette = _p; // before the pages are created, so they pick up the theme's colours

        _pluginsPage = new PluginsPage();
        _commonPages = new SettingsPage[] { new GeneralPage(), new WidgetPage(), new ShortcutsPage(), new WindowsPage(), new NotificationsPage(), _pluginsPage };
        foreach (var page in _commonPages.Append(_aboutPage)) page.LoadFrom(Settings);
        _pluginsPage.ActivationChanged += RebuildPages;
        _pluginsPage.ConfigureRequested += key => { if (_pluginPages.TryGetValue(key, out var page)) _ = ShowPageAsync(Array.IndexOf(_pages, page)); };

        Text = L.T("LabWidge – Settings", "LabWidge – Indstillinger");
        Icon = AppIconProvider.GetIcon();
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5F);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(860, 680);
        MinimumSize = new Size(760, 520);
        BackColor = SettingsTheme.PageColor(_p);
        ForeColor = _p.TextPrimary;

        _menu = new NavMenu(_p, Array.Empty<(string Glyph, string Title)>(), new Dictionary<int, string>()) { Dock = DockStyle.Left, Width = 230 };
        _menu.Selected += async index => await ShowPageAsync(index);

        // Bottom bar
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = SettingsTheme.CardColor(_p) };
        bottom.Paint += (_, e) =>
        {
            using var line = new Pen(_p.Line);
            e.Graphics.DrawLine(line, 0, 0, bottom.Width, 0);
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(0, 12, 16, 10),
            BackColor = Color.Transparent
        };
        var save = new Button { Text = L.T("Save", "Gem"), Width = 104, Height = 32, Tag = "accent" };
        var cancel = new Button { Text = L.T("Cancel", "Annuller"), Width = 104, Height = 32, DialogResult = DialogResult.Cancel, Margin = new Padding(0, 0, 8, 0) };
        buttons.Controls.AddRange(new Control[] { save, cancel });
        var wizard = new LinkLabel { Text = L.T("Run the setup guide again", "Kør opsætningsguiden igen"), AutoSize = true, Location = new Point(20, 20), BackColor = Color.Transparent };
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(wizard);

        save.Click += (_, _) => SaveAndClose();
        wizard.LinkClicked += (_, _) =>
        {
            RunOnboardingRequested = true;
            DialogResult = DialogResult.Abort;
            Close();
        };

        RebuildPages();
        SettingsTheme.Apply(bottom, _p);

        Controls.Add(_host);
        Controls.Add(bottom);
        Controls.Add(_menu);
        AcceptButton = save;
        CancelButton = cancel;

        HandleCreated += (_, _) => SettingsTheme.TitleBar(this, _p);
        Shown += async (_, _) => await ShowPageAsync(0);
    }

    private void RebuildPages()
    {
        var current = _index >= 0 && _index < _pages.Length ? _pages[_index] : null;
        var active = new List<SettingsPage>();
        foreach (var plugin in WidgetPlugins.All.Where(p => Settings.IsPluginEnabled(p.Key)))
        {
            if (!_pluginPages.TryGetValue(plugin.Key, out var page))
            {
                page = plugin.CreateSettingsPage();
                page.LoadFrom(Settings);
                page.UsePluginActivation();
                _pluginPages.Add(plugin.Key, page);
            }
            active.Add(page);
        }
        _pages = _commonPages.Concat(active).Append(_aboutPage).ToArray();
        foreach (var page in _pages)
        {
            if (page.Parent != _host)
            {
                page.Visible = false;
                page.Dock = DockStyle.Fill;
                _host.Controls.Add(page);
            }
            SettingsTheme.Apply(page, _p);
        }
        var groups = new Dictionary<int, string> { [0] = L.T("GENERAL", "GENERELT"), [_commonPages.Length] = "PLUGINS" };
        _menu.SetItems(_pages.Select(p => (p.Glyph, p.Title)).ToArray(), groups);
        var index = current == null ? 0 : Array.IndexOf(_pages, current);
        _index = -1;
        _ = ShowPageAsync(index < 0 ? Array.IndexOf(_pages, _pluginsPage) : index);
    }

    private async Task ShowPageAsync(int index)
    {
        if (index < 0 || index >= _pages.Length || index == _index) return;
        _index = index;
        _menu.SelectedIndex = index;
        var page = _pages[index];
        _host.SuspendLayout();
        foreach (Control child in _host.Controls) child.Visible = ReferenceEquals(child, page);
        page.BringToFront();
        _host.ResumeLayout();
        if (_shown.Add(page))
        {
            await page.OnFirstShownAsync();
            if (IsDisposed || page.IsDisposed) return;
            SettingsTheme.Apply(page, _p); // controls created while loading (e.g. lists) get the theme too
        }
    }

    private void SaveAndClose()
    {
        for (var i = 0; i < _pages.Length; i++)
        {
            var error = _pages[i].SaveTo(Settings);
            if (error != null)
            {
                _ = ShowPageAsync(i);
                MessageBox.Show(this, error, L.T("Settings", "Indstillinger"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>The settings menu: the app at the top, then the pages with icons in groups. The chosen page is highlighted.</summary>
internal sealed class NavMenu : Control
{
    private const int ItemHeight = 36;
    private readonly Palette _p;
    private (string Glyph, string Title)[] _items;
    private Dictionary<int, string> _groups;
    private float _scroll;
    private float _contentHeight;
    private readonly Font _icon;
    private readonly Font _title = new("Segoe UI Semibold", 12.5F);
    private readonly Font _group = new("Segoe UI Semibold", 7.5F);
    private readonly List<RectangleF> _rects = new();
    private int _hover = -1;
    private int _selected;

    public event Action<int>? Selected;

    public NavMenu(Palette p, (string Glyph, string Title)[] items, Dictionary<int, string> groups)
    {
        _p = p;
        _items = items;
        _groups = groups;
        _icon = new Font(PanelFonts.HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 11F);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = p.IsDark ? Color.FromArgb(14, 17, 23) : Color.FromArgb(233, 237, 242);
        Cursor = Cursors.Hand;
    }

    public void SetItems((string Glyph, string Title)[] items, Dictionary<int, string> groups)
    {
        _items = items; _groups = groups; _scroll = 0; Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll = Math.Clamp(_scroll - e.Delta / 120f * S(76), 0, Math.Max(0, _contentHeight - Height));
        Invalidate();
    }

    public int SelectedIndex
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    private float S(float v) => v * DeviceDpi / 96f;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _rects.Clear();

        // The app
        using (var logo = WidgetIcon.Render((int)S(64), WidgetIcon.Amber))
            g.DrawImage(logo, new RectangleF(S(20), S(22), S(34), S(34)));
        TextRenderer.DrawText(g, "LabWidge", _title, new Point((int)S(64), (int)S(22)), _p.TextPrimary, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, L.T("Settings", "Indstillinger"), Font, new Point((int)S(64), (int)S(42)), _p.TextSecondary, TextFormatFlags.NoPadding);

        var y = S(80) - _scroll;
        var clip = g.Save();
        g.SetClip(new RectangleF(0, S(76), Width, Math.Max(0, Height - S(76))));
        for (var i = 0; i < _items.Length; i++)
        {
            if (_groups.TryGetValue(i, out var group))
            {
                y += S(10);
                if (group.Length > 0)
                {
                    TextRenderer.DrawText(g, group, _group, new Point((int)S(24), (int)y), _p.TextDim, TextFormatFlags.NoPadding);
                    y += S(20);
                }
                else
                {
                    using var line = new Pen(_p.Line);
                    g.DrawLine(line, S(20), y, Width - S(20), y);
                    y += S(10);
                }
            }
            var r = new RectangleF(S(12), y, Width - S(24), S(ItemHeight));
            _rects.Add(r);
            var selected = i == _selected;
            if (selected || i == _hover)
            {
                using var path = WidgetIcon.RoundedRect(r, S(6));
                using var fill = new SolidBrush(selected ? Color.FromArgb(_p.IsDark ? 52 : 38, _p.Blue) : _p.HoverBg);
                g.FillPath(fill, path);
                if (selected)
                {
                    using var bar = WidgetIcon.RoundedRect(new RectangleF(r.X, r.Y + S(9), S(3), r.Height - S(18)), S(1.5f));
                    using var accent = new SolidBrush(_p.Blue);
                    g.FillPath(accent, bar);
                }
            }
            var color = selected ? _p.TextPrimary : _p.TextSecondary;
            TextRenderer.DrawText(g, _items[i].Glyph, _icon, new Point((int)(r.X + S(14)), (int)(r.Y + S(10))), selected ? _p.Blue : color, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _items[i].Title, Font, new Point((int)(r.X + S(42)), (int)(r.Y + S(9))), color, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            y += S(ItemHeight + 2);
        }

        _contentHeight = y + _scroll + S(12);
        g.Restore(clip);
        if (_contentHeight > Height)
        {
            var track = Math.Max(1, Height - S(80));
            var thumb = Math.Max(S(24), track * track / (_contentHeight - S(80)));
            var top = S(80) + (track - thumb) * (_scroll / (_contentHeight - Height));
            using var brush = new SolidBrush(_p.TextDim);
            g.FillRectangle(brush, Width - S(5), top, S(2), thumb);
        }
        using var edge = new Pen(_p.Line);
        g.DrawLine(edge, Width - 1, 0, Width - 1, Height);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = e.Y >= S(76) ? _rects.FindIndex(r => r.Contains(e.Location)) : -1;
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var index = e.Y >= S(76) ? _rects.FindIndex(r => r.Contains(e.Location)) : -1;
        if (index >= 0) Selected?.Invoke(index);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _title.Dispose();
            _group.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Setup guide for new users.</summary>
internal sealed class OnboardingForm : Form
{
    private readonly SettingsPage[] _pages;
    private readonly string[] _stepNames =
    {
        L.T("Welcome", "Velkommen"), L.T("General", "Generelt"), L.T("Electricity price", "Elpris"), "Home Assistant", "Cloudflare", L.T("Notifications", "Notifikationer")
    };
    private readonly HashSet<SettingsPage> _shown = new();
    private readonly Panel _host = new() { Dock = DockStyle.Fill };
    private readonly Label[] _stepLabels;
    private readonly Button _back = new() { Text = L.T("Back", "Tilbage"), Width = 96, Height = 32 };
    private readonly Button _next = new() { Text = L.T("Next", "Næste"), Width = 110, Height = 32, Tag = "accent" };
    private readonly Label _progress = new() { AutoSize = true, Margin = new Padding(0, 9, 0, 0) };
    private readonly Palette _p;
    private int _index;

    public AppSettings Settings { get; }

    public OnboardingForm(AppSettings settings)
    {
        Settings = settings.Clone();
        _p = Palette.For(settings.Theme);
        Ui.Palette = _p; // before the pages are created
        _pages = new SettingsPage[] { new WelcomePage(), new GeneralPage(), new PricePage(), new HomeAssistantPage(), new CloudflarePage(), new NotificationsPage() };

        Text = L.T("LabWidge – Setup", "LabWidge – Opsætning");
        Icon = AppIconProvider.GetIcon();
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5F);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(820, 640);
        BackColor = SettingsTheme.PageColor(_p);
        ForeColor = _p.TextPrimary;
        _progress.ForeColor = _p.TextSecondary;

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
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = SettingsTheme.CardColor(_p) };
        bottom.Paint += (_, e) =>
        {
            using var line = new Pen(_p.Line);
            e.Graphics.DrawLine(line, 0, 0, bottom.Width, 0);
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(0, 11, 16, 10),
            BackColor = Color.Transparent
        };
        _back.Margin = new Padding(0, 0, 8, 0);
        buttons.Controls.AddRange(new Control[] { _next, _back });
        _progress.AutoSize = false;
        _progress.Dock = DockStyle.Fill;
        _progress.TextAlign = ContentAlignment.MiddleLeft;
        _progress.Padding = new Padding(16, 0, 0, 0);
        bottom.Controls.Add(_progress);
        bottom.Controls.Add(buttons);

        _back.Click += async (_, _) => await GoToAsync(_index - 1);
        _next.Click += async (_, _) => await NextAsync();

        foreach (var page in _pages)
        {
            page.LoadFrom(Settings);
            SettingsTheme.Apply(page, _p);
        }
        SettingsTheme.Apply(bottom, _p);

        Controls.Add(_host);
        Controls.Add(bottom);
        Controls.Add(sidebar);
        AcceptButton = _next;

        HandleCreated += (_, _) => SettingsTheme.TitleBar(this, _p);
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
        _pages[index].Dock = DockStyle.Fill;
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

        if (_shown.Add(_pages[index]))
        {
            await _pages[index].OnFirstShownAsync();
            SettingsTheme.Apply(_pages[index], _p);
        }
    }
}
