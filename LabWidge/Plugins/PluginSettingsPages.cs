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

    public PluginsPage()
    {
        Add(Ui.Heading("Plugins"), Ui.Help(L.T(
            "Choose the plugins you use. Disabled plugins stop collecting data and keep their settings. Changes take effect when you save.",
            "Vælg de plugins, du bruger. Deaktiverede plugins stopper dataindsamling og beholder deres indstillinger. Ændringer træder i kraft, når du gemmer.")));
        foreach (var plugin in WidgetPlugins.All)
        {
            var toggle = Ui.Check(L.T("Active", "Aktiv"));
            var configure = new Button { Text = L.T("Configure", "Indstillinger"), AutoSize = true };
            _switches[plugin.Key] = toggle;
            _configure[plugin.Key] = configure;
            Add(Ui.Section(plugin.Title), Ui.Help(plugin.Description), toggle, configure);
            configure.Click += (_, _) => ConfigureRequested?.Invoke(plugin.Key);
            toggle.CheckedChanged += (_, _) =>
            {
                configure.Enabled = toggle.Checked;
                if (_loading || _settings == null) return;
                _settings.SetPluginEnabled(plugin.Key, toggle.Checked);
                ActivationChanged?.Invoke();
            };
        }
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
    private readonly CheckedListBox _disks = new() { Width = Ui.ContentWidth, Height = 120, CheckOnClick = true };
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
