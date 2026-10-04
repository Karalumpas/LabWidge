using System.Drawing;

internal sealed class PricePlugin : WidgetPlugin
{
    public override string Key => "price";
    public override string Title => L.T("Electricity price", "Elpris");
    public override string Glyph => "";
    public override string Description => L.T("Spot prices, tariffs, charts and price alerts.", "Spotpriser, tariffer, grafer og prisalarmer.");
    public override bool IsVisible(DashboardForm h, AppSettings s) => s.PriceEnabled;
    public override float RenderExpanded(DashboardForm h, Graphics g, float x, float y, float w) => h.DrawPrice(g, x, y, w);
    public override PopupPanel? CreateWindow(DashboardForm h, AppSettings s, Action save) => s.PriceCountry == null ? null : new PricePanel(h.Services.Price, s, save);
    public override SettingsPage CreateSettingsPage() => new PricePage();
    public override IEnumerable<PluginJob> CreateJobs(PluginServices c, AppSettings s)
    {
        if (s.PriceCountry == null) yield break;
        var first = true;
        yield return new("prices", 30_000, async ct => { if (first || c.Price.NeedsRefresh(s)) { first = false; await c.Price.RefreshAsync(s, ct); } ct.ThrowIfCancellationRequested(); c.PriceTick(); });
    }
}
internal sealed class HomeAssistantPlugin : WidgetPlugin
{
    public override string Key => "ha";
    public override string Title => "Home Assistant";
    public override string Glyph => "";
    public override string Description => L.T("Entities, switches and your Home Assistant dashboard.", "Entiteter, kontakter og dit Home Assistant-dashboard.");
    public override bool IsVisible(DashboardForm h, AppSettings s) => s.ShowHomeAssistant && s.HasHomeAssistant;
    public override float RenderExpanded(DashboardForm h, Graphics g, float x, float y, float w) => h.DrawHomeAssistant(g, x, y, w);
    public override PopupPanel? CreateWindow(DashboardForm h, AppSettings s, Action save) => s.HasHomeAssistant ? new HomeAssistantPanel(h.Services.HomeAssistant, s, save) : null;
    public override SettingsPage CreateSettingsPage() => new HomeAssistantPage();
    public override IEnumerable<PluginJob> CreateJobs(PluginServices c, AppSettings s)
    {
        if (s.HasHomeAssistant) yield return new("entities", Math.Clamp(s.HomeAssistantRefreshSeconds, 5, 600) * 1000,
            ct => (c.WidgetVisible() && s.ShowHomeAssistant) || SectionWindows.IsOpen(Key) ? c.HomeAssistant.RefreshAsync(s, ct) : Task.CompletedTask);
    }
}
internal sealed class CloudflarePlugin : WidgetPlugin
{
    public override string Key => "cloudflare";
    public override string Title => "Cloudflare";
    public override string Glyph => "";
    public override string Description => L.T("DNS updates, tunnels and service monitoring.", "DNS-opdateringer, tunneller og overvågning af tjenester.");
    public override bool IsVisible(DashboardForm h, AppSettings s) => s.ShowCloudflare && s.HasCloudflare;
    public override float RenderExpanded(DashboardForm h, Graphics g, float x, float y, float w) => h.DrawCloudflare(g, x, y, w);
    public override PopupPanel? CreateWindow(DashboardForm h, AppSettings s, Action save) => s.HasCloudflare ? new CloudflarePanel(h.Services.Cloudflare, h.Services.ServiceMonitor, s, save, h.Services.ExternalIp) : null;
    public override SettingsPage CreateSettingsPage() => new CloudflarePage();
    public override IEnumerable<PluginJob> CreateJobs(PluginServices c, AppSettings s)
    {
        if (!s.HasCloudflare) yield break;
        yield return new("tunnels", Math.Clamp(s.CloudflareRefreshMinutes, 1, 60) * 60_000, ct => c.Cloudflare.RefreshAsync(s, ct));
        yield return new("dns", 300_000, async ct => { await c.RefreshIp(ct); ct.ThrowIfCancellationRequested(); await c.RefreshDns(ct); });
        if (s.ServiceChecksEnabled) yield return new("services", 30_000, c.CheckServices);
    }
}
internal sealed class ProxmoxPlugin : WidgetPlugin
{
    public override string Key => "proxmox";
    public override string Title => "Proxmox";
    public override string Glyph => "";
    public override string Description => L.T("Servers, virtual machines, containers and storage.", "Servere, virtuelle maskiner, containere og lager.");
    public override bool IsVisible(DashboardForm h, AppSettings s) => s.ShowProxmox && s.HasProxmox;
    public override float RenderExpanded(DashboardForm h, Graphics g, float x, float y, float w) => h.DrawProxmox(g, x, y, w);
    public override PopupPanel? CreateWindow(DashboardForm h, AppSettings s, Action save) => s.HasProxmox ? new ProxmoxPanel(h.Services.Proxmox, s, save) : null;
    public override SettingsPage CreateSettingsPage() => new ProxmoxPage();
    public override IEnumerable<PluginJob> CreateJobs(PluginServices c, AppSettings s)
    {
        if (s.HasProxmox) yield return new("cluster", Math.Clamp(s.ProxmoxRefreshSeconds, 5, 600) * 1000,
            ct => (c.WidgetVisible() && s.ShowProxmox) || SectionWindows.IsOpen(Key) ? c.Proxmox.RefreshAsync(s, ct) : Task.CompletedTask);
    }
}
internal sealed class SystemPlugin : WidgetPlugin
{
    public override string Key => "system";
    public override string Title => "System";
    public override string Glyph => "";
    public override string Description => L.T("CPU, memory, graphics card and disks.", "CPU, hukommelse, grafikkort og diske.");
    public override bool IsVisible(DashboardForm h, AppSettings s) => s.ShowSystem;
    public override float RenderExpanded(DashboardForm h, Graphics g, float x, float y, float w) => h.DrawSystem(g, x, y, w);
    public override PopupPanel? CreateWindow(DashboardForm h, AppSettings s, Action save) => new SystemPanel(h.Services.System, s, save);
    public override SettingsPage CreateSettingsPage() => new SystemPluginPage();
    public override IEnumerable<PluginJob> CreateJobs(PluginServices c, AppSettings s)
    {
        yield return new("samples", 1000, _ => { if ((c.WidgetVisible() && s.ShowSystem) || SectionWindows.IsOpen(Key)) { c.System.Sample(s.ShowGpu); c.Redraw(); } return Task.CompletedTask; });
    }
}
internal sealed class NetworkPlugin : WidgetPlugin
{
    public override string Key => "network";
    public override string Title => L.T("Network", "Netværk");
    public override string Glyph => "";
    public override string Description => L.T("IP addresses, traffic and latency.", "IP-adresser, trafik og svartid.");
    public override bool IsVisible(DashboardForm h, AppSettings s) => s.ShowNetwork;
    public override float RenderExpanded(DashboardForm h, Graphics g, float x, float y, float w) => h.DrawNetwork(g, x, y, w);
    public override PopupPanel? CreateWindow(DashboardForm h, AppSettings s, Action save) => new NetworkPanel(h.Services.Network, h.Services.ExternalIp, h.Services.NetworkState, s, save);
    public override SettingsPage CreateSettingsPage() => new NetworkPluginPage();
    public override IEnumerable<PluginJob> CreateJobs(PluginServices c, AppSettings s)
    {
        yield return new("traffic", 1000, ct => { if ((c.WidgetVisible() && s.ShowNetwork) || SectionWindows.IsOpen(Key)) { c.Network.PingTarget = s.PingTarget; c.Network.Sample(ct); c.Redraw(); } return Task.CompletedTask; });
        yield return new("external-ip", 300_000, c.RefreshIp);
    }
}
internal sealed class AudioPlugin : WidgetPlugin
{
    public override string Key => "audio";
    public override string Title => L.T("Audio", "Lyd");
    public override string Glyph => "";
    public override string Description => L.T("Outputs, microphone, volume and headset battery.", "Lydudgange, mikrofon, lydstyrke og headsetbatteri.");
    public override bool IsVisible(DashboardForm h, AppSettings s) => s.ShowAudio && h.VisibleAudioDevices().Count > 0;
    public override float RenderExpanded(DashboardForm h, Graphics g, float x, float y, float w) => h.DrawAudio(g, x, y, w);
    public override PopupPanel? CreateWindow(DashboardForm h, AppSettings s, Action save) => new AudioPanel(h.Services.Audio, h.Services.SwitchAudio, s, save);
    public override SettingsPage CreateSettingsPage() => new AudioPage();
    public override IEnumerable<PluginJob> CreateJobs(PluginServices c, AppSettings s)
    {
        yield return new("devices", 2000, _ => { c.RefreshAudio(); c.Redraw(); return Task.CompletedTask; });
        yield return new("battery", 60_000, c.RefreshBatteries);
    }
}
