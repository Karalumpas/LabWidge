using System.Drawing;

/// <summary>Internal extension contract. Layout, dragging, pinning and persistence belong to the host.</summary>
internal interface IWidgetPlugin
{
    string Key { get; }
    string Title { get; }
    string Glyph { get; }
    string Description { get; }
    bool IsVisible(DashboardForm host, AppSettings settings);
    float RenderExpanded(DashboardForm host, Graphics g, float x, float y, float width);
    float RenderCompact(DashboardForm host, Graphics g, float x, float y, float width);
    PopupPanel? CreateWindow(DashboardForm host, AppSettings settings, Action save);
    SettingsPage CreateSettingsPage();
    IEnumerable<PluginJob> CreateJobs(PluginServices services, AppSettings settings);
}

internal abstract class WidgetPlugin : IWidgetPlugin
{
    public abstract string Key { get; }
    public abstract string Title { get; }
    public abstract string Glyph { get; }
    public abstract string Description { get; }
    public abstract bool IsVisible(DashboardForm host, AppSettings settings);
    public abstract float RenderExpanded(DashboardForm host, Graphics g, float x, float y, float width);
    public float RenderCompact(DashboardForm host, Graphics g, float x, float y, float width) => host.RenderPluginSummary(this, g, x, y, width);
    public abstract PopupPanel? CreateWindow(DashboardForm host, AppSettings settings, Action save);
    public abstract SettingsPage CreateSettingsPage();
    public abstract IEnumerable<PluginJob> CreateJobs(PluginServices services, AppSettings settings);
}

internal static class WidgetPlugins
{
    public static readonly IReadOnlyList<IWidgetPlugin> All = new IWidgetPlugin[]
    {
        new PricePlugin(), new HomeAssistantPlugin(), new CloudflarePlugin(), new ProxmoxPlugin(), new SystemPlugin(), new NetworkPlugin(), new AudioPlugin()
    };
    public static IWidgetPlugin? Find(string key) => All.FirstOrDefault(p => p.Key == key);
}

internal sealed class PluginServices
{
    public required ElectricityPriceService Price { get; init; }
    public required SystemMonitor System { get; init; }
    public required NetworkMonitor Network { get; init; }
    public required AudioService Audio { get; init; }
    public required HomeAssistantService HomeAssistant { get; init; }
    public required CloudflareService Cloudflare { get; init; }
    public required ServiceMonitor ServiceMonitor { get; init; }
    public required ProxmoxService Proxmox { get; init; }
    public required Func<string?> ExternalIp { get; init; }
    public required Func<(DateTime Time, string? Error)> NetworkState { get; init; }
    public required Action<string> SwitchAudio { get; init; }
    public Action Redraw { get; set; } = () => { };
    public Func<bool> WidgetVisible { get; set; } = () => true;
    public Action PriceTick { get; set; } = () => { };
    public Func<CancellationToken, Task> RefreshIp { get; set; } = _ => Task.CompletedTask;
    public Func<CancellationToken, Task> CheckServices { get; set; } = _ => Task.CompletedTask;
    public Func<CancellationToken, Task> RefreshCloudflare { get; set; } = _ => Task.CompletedTask;
    public Action RefreshAudio { get; set; } = () => { };
    public Func<CancellationToken, Task> RefreshBatteries { get; set; } = _ => Task.CompletedTask;
}
