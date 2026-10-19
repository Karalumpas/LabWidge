using System.Windows.Forms;

internal sealed class PluginsPage : SettingsPage
{
    private readonly Dictionary<string, CheckBox> _switches = new();
    private readonly Dictionary<string, Button> _configure = new();
    private AppSettings? _settings;
    private bool _loading;
    public event Action? ActivationChanged;
    public event Action<string>? ConfigureRequested;
    public override string Title => "Plugins";
    public override string Glyph => "";

    /// <summary>The plugins in groups, so the page reads as "what do you want to see".</summary>
    private static readonly (string Caption, string[] Keys)[] Groups =
    {
        (L.T("ELECTRICITY", "ELPRIS"), new[] { "price" }),
        (L.T("YOUR PC", "DIN PC"), new[] { "system", "network", "audio" }),
        (L.T("HOME LAB", "HJEMMELAB"), new[] { "ha", "cloudflare", "proxmox" }),
    };

    public PluginsPage()
    {
        Add(Ui.Heading("Plugins"), Ui.Help(L.T(
            "Each plugin is a section in the widget with its own window and settings. Turn on the ones you use – a plugin you turn off keeps its settings " +
            "and stops all its background work. Changes take effect when you save.\n\nTip: click the gear by a section's title in the widget to go straight to its settings.",
            "Hvert plugin er en sektion i widgetten med sit eget vindue og sine egne indstillinger. Slå dem til, du bruger – et plugin, du slår fra, beholder " +
            "sine indstillinger og stopper alt sit arbejde i baggrunden. Ændringer træder i kraft, når du gemmer.\n\nTip: Klik på tandhjulet ved en sektions titel i widgetten for at gå direkte til dens indstillinger.")));
        var grouped = Groups.SelectMany(g => g.Keys).ToHashSet();
        var groups = Groups.Append((L.T("OTHER", "ANDET"), WidgetPlugins.All.Select(p => p.Key).Where(k => !grouped.Contains(k)).ToArray()));
        foreach (var (caption, keys) in groups)
        {
            var plugins = keys.Select(WidgetPlugins.Find).OfType<IWidgetPlugin>().ToList();
            if (plugins.Count == 0) continue;
            EndCard();
            var label = Ui.Help(caption);
            label.Font = new System.Drawing.Font("Segoe UI Semibold", 8F);
            label.Margin = new Padding(2, 14, 0, 0);
            Add(label);
            foreach (var plugin in plugins) AddPlugin(plugin);
        }
    }

    private void AddPlugin(IWidgetPlugin plugin)
    {
        var toggle = Ui.Check(L.T("Use this plugin", "Brug dette plugin"));
        var configure = new Button { Text = L.T("Settings ›", "Indstillinger ›"), AutoSize = true };
        _switches[plugin.Key] = toggle;
        _configure[plugin.Key] = configure;
        Add(Ui.Section(plugin.Title), Ui.Help(plugin.Description));
        // A plugin with pages of its own lists them, so the user knows where to find them
        var subPages = plugin.CreateSubPages().ToList();
        if (subPages.Count > 0) Add(Ui.Help(L.T("Includes: ", "Indeholder: ") + string.Join(" · ", subPages.Select(p => p.Page.Title))));
        foreach (var (_, page) in subPages) page.Dispose();
        Add(toggle, ButtonRow.Of(configure));
        configure.Click += (_, _) => ConfigureRequested?.Invoke(plugin.Key);
        toggle.CheckedChanged += (_, _) =>
        {
            configure.Enabled = toggle.Checked;
            if (_loading || _settings == null) return;
            _settings.SetPluginEnabled(plugin.Key, toggle.Checked);
            ActivationChanged?.Invoke();
        };
    }

    public override void LoadFrom(AppSettings s)
    {
        _settings = s; _loading = true;
        foreach (var plugin in WidgetPlugins.All)
        {
            _switches[plugin.Key].Checked = s.IsPluginEnabled(plugin.Key);
            _configure[plugin.Key].Enabled = s.IsPluginEnabled(plugin.Key);
        }
        _loading = false;
    }
    public override string? SaveTo(AppSettings s)
    {
        foreach (var plugin in WidgetPlugins.All) s.SetPluginEnabled(plugin.Key, _switches[plugin.Key].Checked);
        return null;
    }
}

internal sealed class SystemPluginPage : SettingsPage
{
    private readonly CheckBox _show = Ui.Check(L.T("Show in the widget", "Vis i widgetten"));
    private readonly CheckBox _gpu = Ui.Check(L.T("Monitor the graphics card", "Overvåg grafikkortet"));
    private readonly CheckedListBox _disks = new() { Width = Ui.FieldWidth, Height = 120, CheckOnClick = true };
    private string[] _hidden = Array.Empty<string>();
    public override string Title => "System";
    public override string Glyph => "";
    public SystemPluginPage()
    {
        Add(Ui.Heading("System"), Ui.Help(L.T("CPU, memory, graphics card and disks.", "CPU, hukommelse, grafikkort og diske.")),
            Ui.Section(L.T("Monitoring", "Overvågning")), _show, _gpu,
            Ui.Row(L.T("Disks in the widget", "Diske i widgetten"), _disks));
    }
    public override void LoadFrom(AppSettings s)
    {
        _show.Checked = s.ShowSystem; _gpu.Checked = s.ShowGpu; _hidden = s.HiddenDisks;
        _disks.Items.Clear();
        try
        {
            foreach (var name in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => AppSettings.DiskName(d.Name)))
                _disks.Items.Add(name, !_hidden.Contains(name, StringComparer.OrdinalIgnoreCase));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public override string? SaveTo(AppSettings s)
    {
        s.ShowSystem = _show.Checked; s.ShowGpu = _gpu.Checked;
        var listed = _disks.Items.Cast<string>().ToArray();
        s.HiddenDisks = _hidden.Where(h => !listed.Contains(h, StringComparer.OrdinalIgnoreCase))
            .Concat(listed.Where((_, i) => !_disks.GetItemChecked(i))).ToArray();
        return null;
    }
}

internal sealed class NetworkPluginPage : SettingsPage
{
    private readonly CheckBox _show = Ui.Check(L.T("Show in the widget", "Vis i widgetten"));
    private readonly CheckBox _virtual = Ui.Check(L.T("Show virtual network adapters", "Vis virtuelle netværkskort"));
    private readonly TextBox _ping = new() { Width = 250 };
    public override string Title => L.T("Network", "Netværk");
    public override string Glyph => "";
    public NetworkPluginPage()
    {
        Add(Ui.Heading(Title), Ui.Help(L.T("IP addresses, traffic and latency.", "IP-adresser, trafik og svartid.")),
            Ui.Section(L.T("Monitoring", "Overvågning")), _show, _virtual,
            Ui.Row(L.T("Measure ping to", "Mål ping til"), _ping),
            Ui.Help(L.T("A host name or IP address, for example your router or 1.1.1.1.", "Et værtsnavn eller en IP-adresse, fx din router eller 1.1.1.1.")));
    }
    public override void LoadFrom(AppSettings s) { _show.Checked = s.ShowNetwork; _virtual.Checked = s.ShowVirtualAdapters; _ping.Text = s.PingTarget; }
    public override string? SaveTo(AppSettings s)
    {
        var ping = _ping.Text.Trim();
        if (ping.Length == 0 || ping.Length > 253 || ping.Any(char.IsWhiteSpace))
            return L.T("Type a host name or IP address to measure ping to.", "Skriv et værtsnavn eller en IP-adresse at måle ping til.");
        s.ShowNetwork = _show.Checked; s.ShowVirtualAdapters = _virtual.Checked; s.PingTarget = ping;
        return null;
    }
}
