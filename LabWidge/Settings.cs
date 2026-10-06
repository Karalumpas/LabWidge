using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum WidgetTheme { System, Light, Dark }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum SectionPin { None, Top, Bottom }

/// <summary>Where the shortcuts are: docked as a rail on one side of the widget, or as a section among the others.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum RailPlacement { Left, Right, Section }

/// <summary>Where a section window was and whether it is pinned. Stored per section in the settings.</summary>
internal sealed class SectionWindowState
{
    /// <summary>Pinned: stays open when you click elsewhere, and opens again when LabWidge starts.</summary>
    public bool Pinned { get; set; }
    /// <summary>Was open when LabWidge closed (only used for pinned windows).</summary>
    public bool Open { get; set; }
    public int? Left { get; set; }
    public int? Top { get; set; }
    /// <summary>Size in pixels chosen by dragging an edge; null follows the content.</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }
}

/// <summary>
/// Everything the user can set. The property names are the keys in settings.json and must not be renamed,
/// or existing users lose those settings when they update.
/// </summary>
internal sealed class AppSettings
{
    public const int CurrentVersion = 2;

    public int SettingsVersion { get; set; } = CurrentVersion;
    public bool OnboardingCompleted { get; set; }
    /// <summary>Explicit plugin activation. Missing keys use the existing preferences for backwards compatibility.</summary>
    public Dictionary<string, bool> Plugins { get; set; } = new();

    public bool IsPluginEnabled(string key) => Plugins.TryGetValue(key, out var enabled) ? enabled : key switch
    {
        "price" => ShowPrice, "system" => ShowSystem, "network" => ShowNetwork, "audio" => ShowAudio,
        "ha" => HomeAssistantEnabled, "cloudflare" => CloudflareEnabled, "proxmox" => ProxmoxEnabled,
        _ => false
    };

    public void SetPluginEnabled(string key, bool enabled)
    {
        var previouslyEnabled = IsPluginEnabled(key);
        var legacyActivation = !Plugins.ContainsKey(key);
        Plugins[key] = enabled;
        // Connection switches remain readable by old settings and the setup guide.
        if (enabled)
        {
            if (!previouslyEnabled && legacyActivation)
            {
                switch (key)
                {
                    case "price": ShowPrice = true; break;
                    case "system": ShowSystem = true; break;
                    case "network": ShowNetwork = true; break;
                    case "audio": ShowAudio = true; break;
                }
            }
            switch (key)
            {
                case "ha": HomeAssistantEnabled = true; break;
                case "cloudflare": CloudflareEnabled = true; break;
                case "proxmox": ProxmoxEnabled = true; break;
            }
        }
    }
    public bool StartWithWindows { get; set; } = true;
    /// <summary>"en" or "da". Null until chosen in the installer or the settings; then the Windows language is used.</summary>
    public string? Language { get; set; }

    // Cloudflare (optional)
    public bool CloudflareEnabled { get; set; }
    public string? ZoneId { get; set; }
    public bool CloudflareAutoUpdate { get; set; } = true;
    public bool UpdateAllARecords { get; set; } = true;
    public string[] IncludedHosts { get; set; } = Array.Empty<string>();
    /// <summary>Account id for tunnels. Empty = looked up through the zone, if the token may read it.</summary>
    public string? CloudflareAccountId { get; set; }
    public bool ShowCloudflare { get; set; } = true;
    public bool CollapsedCloudflare { get; set; }
    /// <summary>Check that the addresses behind the tunnels respond.</summary>
    public bool ServiceChecksEnabled { get; set; } = true;
    /// <summary>Addresses not to check, e.g. services that are switched off on purpose.</summary>
    public string[] IgnoredServices { get; set; } = Array.Empty<string>();

    // Proxmox (optional)
    public bool ProxmoxEnabled { get; set; }
    /// <summary>E.g. https://192.168.1.50:8006.</summary>
    public string? ProxmoxUrl { get; set; }
    /// <summary>The API token id, e.g. widget@pve!widget. The secret itself is kept in Credential Manager.</summary>
    public string? ProxmoxTokenId { get; set; }
    public bool ProxmoxAllowSelfSigned { get; set; } = true;
    /// <summary>SHA-256 fingerprint of the self-signed certificate that was accepted the first time.</summary>
    public string? ProxmoxCertThumbprint { get; set; }
    public bool ShowProxmox { get; set; } = true;
    public bool CollapsedProxmox { get; set; }

    // Widget
    public bool WidgetVisible { get; set; } = true;
    public bool WidgetTopMost { get; set; } = true;
    public int? WidgetLeft { get; set; }
    public int? WidgetTop { get; set; }
    /// <summary>The widget's bottom edge. The widget is anchored at the bottom, so this is stored rather than the top.</summary>
    public int? WidgetBottom { get; set; }
    public bool ShowPrice { get; set; } = true;
    public bool ShowSystem { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;
    public bool ShowHomeAssistant { get; set; } = true;
    public bool CollapsedHomeAssistant { get; set; } = true;
    public bool CollapsedPrice { get; set; }
    public bool CollapsedSystem { get; set; }
    public bool CollapsedNetwork { get; set; }
    public bool CompactMode { get; set; }
    /// <summary>Section order in the widget (keys from DashboardForm.DefaultSectionOrder). Null = default.</summary>
    public string[]? SectionOrder { get; set; }
    /// <summary>Size in logical pixels; null follows the content automatically.</summary>
    public int? WidgetWidth { get; set; }
    public int? WidgetHeight { get; set; }
    public Dictionary<string, SectionPin> SectionPins { get; set; } = new();
    /// <summary>The side each section was last pinned to – a click on the pin uses it again.</summary>
    public Dictionary<string, SectionPin> SectionLastPins { get; set; } = new();
    public string[] SectionPinSummaries { get; set; } = Array.Empty<string>();
    /// <summary>Pin, position and size of each section's window (key = section, e.g. "price").</summary>
    public Dictionary<string, SectionWindowState> SectionWindows { get; set; } = new();
    /// <summary>Windows the user chose to forget in the settings window – applied when the settings are saved, never stored.</summary>
    [JsonIgnore]
    public string[] ForgetWindows { get; set; } = Array.Empty<string>();
    /// <summary>Open the pinned section windows again when LabWidge starts.</summary>
    public bool RestorePinnedWindows { get; set; } = true;
    /// <summary>Show the rail with <see cref="LaunchItems"/> on the left of the widget.</summary>
    public bool ShowLaunchRail { get; set; } = true;
    /// <summary>Websites and programs in the rail, top to bottom.</summary>
    public List<LaunchItem> LaunchItems { get; set; } = new();
    public RailPlacement LaunchRailPlacement { get; set; } = RailPlacement.Left;
    /// <summary>Where the docked drawer's middle sits, as a share of the widget's height (0.5 = centred).</summary>
    public double LaunchRailOffset { get; set; } = 0.5;
    public bool CollapsedShortcuts { get; set; }

    // System and network
    public bool ShowGpu { get; set; } = true;
    /// <summary>Drives (e.g. "D:", see <see cref="DiskName"/>) left out of the widget's system section.</summary>
    public string[] HiddenDisks { get; set; } = Array.Empty<string>();

    /// <summary>The name a disk is shown and hidden by: "C:" (DriveInfo.Name is "C:\").</summary>
    public static string DiskName(string driveName) => driveName.TrimEnd('\\');
    /// <summary>Show virtual adapters (VPN, Hyper-V, WSL) among the widget's local addresses.</summary>
    public bool ShowVirtualAdapters { get; set; } = true;
    /// <summary>The host the latency is measured against, e.g. 1.1.1.1 or the router.</summary>
    public string PingTarget { get; set; } = "1.1.1.1";

    // How often the home lab services are asked
    public int HomeAssistantRefreshSeconds { get; set; } = 15;
    public int ProxmoxRefreshSeconds { get; set; } = 15;
    public int CloudflareRefreshMinutes { get; set; } = 2;

    // Audio
    public bool ShowAudio { get; set; } = true;
    public bool CollapsedAudio { get; set; }
    /// <summary>Audio outputs shown in the widget, in order. Null = all except virtual ones.</summary>
    public string[]? AudioDeviceIds { get; set; }
    /// <summary>Custom names for audio outputs (device id → name).</summary>
    public Dictionary<string, string> AudioNames { get; set; } = new();
    /// <summary>The default device: chosen when Windows starts and when the active device disappears.</summary>
    public string? AudioStandardId { get; set; }
    /// <summary>Colour of the battery fill on the headset button as "#RRGGBB". Null = green.</summary>
    public string? AudioBatteryColor { get; set; }
    /// <summary>Turn the battery fill orange/red at low levels, whatever colour is chosen.</summary>
    public bool AudioBatteryWarn { get; set; } = true;
    /// <summary>Notify when the headset battery reaches <see cref="AudioBatteryLowLevel"/> and again at <see cref="AudioBatteryCriticalLevel"/>.</summary>
    public bool AudioBatteryAlert { get; set; } = true;
    public int AudioBatteryLowLevel { get; set; } = 15;
    public int AudioBatteryCriticalLevel { get; set; } = 5;
    /// <summary>Show the microphone with a mute button in the widget.</summary>
    public bool AudioShowMic { get; set; } = true;
    /// <summary>Show the volume on the buttons and adjust it with the scroll wheel.</summary>
    public bool AudioShowVolume { get; set; } = true;
    /// <summary>Percentage points per notch of the scroll wheel.</summary>
    public int AudioVolumeStep { get; set; } = 2;
    /// <summary>Also switch microphone when the output is switched.</summary>
    public bool AudioSwitchMic { get; set; } = true;
    /// <summary>Microphone per output (output id → microphone id, "" = don't switch). Missing = the microphone in the same device.</summary>
    public Dictionary<string, string> AudioMicPairs { get; set; } = new();
    public WidgetTheme Theme { get; set; } = WidgetTheme.System;
    /// <summary>Opacity in percent (40-100).</summary>
    public int WidgetOpacity { get; set; } = 100;

    // Electricity price
    public string? PostalCode { get; set; }
    /// <summary>ISO code of the country the electricity price is for (see <see cref="Countries"/>), or "OTHER" to hide it.
    /// Null until the app has started once; older settings without it are Danish.</summary>
    public string? Country { get; set; }
    public string PriceArea { get; set; } = "DK1";
    /// <summary>VAT on electricity in percent; null uses the country's usual rate.</summary>
    public double? VatPercent { get; set; }
    public bool PriceInclVat { get; set; } = true;
    public bool PriceShowTotal { get; set; } = true;
    public string? SupplierName { get; set; }
    /// <summary>The electricity supplier's add-on (or other fixed charges) per kWh excluding VAT, in the country's price unit.
    /// The name is kept from when only Danish øre were supported.</summary>
    public double SupplierAddOnOre { get; set; }
    /// <summary>The grid company as ChargeOwner in Energinet's Datahub.</summary>
    public string NetTariffOwner { get; set; } = "";
    public string[] NetTariffCodes { get; set; } = Array.Empty<string>();

    // Home Assistant (optional)
    public bool HomeAssistantEnabled { get; set; }
    /// <summary>Base URL without a trailing slash, e.g. https://home.example.com or http://192.168.0.10:8123.</summary>
    public string? HomeAssistantUrl { get; set; }
    /// <summary>Entities in the order they are shown in the panel. Also used for the summary in the widget.</summary>
    public string[] HomeAssistantEntities { get; set; } = Array.Empty<string>();
    /// <summary>
    /// Path to a Home Assistant dashboard, e.g. /test-panel. When set, the panel shows the dashboard
    /// instead of the list of switches. Empty = the list.
    /// </summary>
    public string? HomeAssistantDashboardPath { get; set; }
    public bool HomeAssistantPanelPinned { get; set; }
    public int HomeAssistantPanelWidth { get; set; } = 460;
    public int HomeAssistantPanelHeight { get; set; } = 760;

    // Notifications
    public bool NotifyIpChange { get; set; } = true;
    public bool NotifyCloudflareError { get; set; } = true;
    public bool NotifyTunnelDown { get; set; } = true;
    public bool NotifyServiceDown { get; set; } = true;
    public bool AlertCheap { get; set; } = true;
    public bool AlertExpensive { get; set; } = true;
    public int AlertLeadMinutes { get; set; } = 30;
    public bool AlertQuietNight { get; set; } = true;
    public DateTime? LastCheapAlertFor { get; set; }
    public DateTime? LastExpensiveAlertFor { get; set; }

    // Updates
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>Download updates in the background and install them when the PC is not in use.</summary>
    public bool AutoInstallUpdates { get; set; } = true;
    /// <summary>A version the user chose to skip, e.g. "1.4.0".</summary>
    public string? SkippedUpdateVersion { get; set; }
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>The version that ran last time – used to show "What's new" after an update.</summary>
    public string? LastRunVersion { get; set; }

    [JsonIgnore]
    public bool HasCloudflare => IsPluginEnabled("cloudflare") && CloudflareEnabled && !string.IsNullOrWhiteSpace(ZoneId);

    [JsonIgnore]
    public bool HasProxmox =>
        IsPluginEnabled("proxmox") && ProxmoxEnabled && !string.IsNullOrWhiteSpace(ProxmoxUrl) && !string.IsNullOrWhiteSpace(ProxmoxTokenId);

    [JsonIgnore]
    public Country? PriceCountry => Countries.Find(Country);

    /// <summary>Whether the electricity price is shown: turned on and the country has prices.</summary>
    [JsonIgnore]
    public bool PriceEnabled => IsPluginEnabled("price") && ShowPrice && PriceCountry != null;

    [JsonIgnore]
    public double EffectiveVatPercent => VatPercent ?? PriceCountry?.VatPercent ?? 0;

    [JsonIgnore]
    public PriceUnit PriceUnit => PriceCountry?.Unit ?? Countries.All[0].Unit;

    [JsonIgnore]
    public bool HasNetTariff => !string.IsNullOrWhiteSpace(NetTariffOwner) && NetTariffCodes.Length > 0;

    [JsonIgnore]
    public bool HasHomeAssistant =>
        IsPluginEnabled("ha") && HomeAssistantEnabled && !string.IsNullOrWhiteSpace(HomeAssistantUrl) && HomeAssistantEntities.Length > 0;

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new AppSettings();
}

internal static class SettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LabWidge",
        "settings.json");

    private static readonly string BackupPath = SettingsPath + ".bak";
    private static readonly string TempPath = SettingsPath + ".tmp";

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath) && !File.Exists(BackupPath))
        {
            return new AppSettings(); // New user: the setup guide is shown
        }

        if (TryLoad(SettingsPath, out var settings))
        {
            return settings;
        }

        // The main file is unreadable – the last known good version is next to it.
        if (TryLoad(BackupPath, out settings))
        {
            Logger.Info("Settings restored from settings.json.bak.");
            Save(settings);
            return settings;
        }

        Logger.Error("No usable settings found – starting with defaults.");
        return new AppSettings();
    }

    private static bool TryLoad(string path, out AppSettings settings)
    {
        settings = new AppSettings();
        try
        {
            if (!File.Exists(path)) return false;

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json);
            if (loaded == null) return false;

            loaded.IncludedHosts ??= Array.Empty<string>();
            loaded.IgnoredServices ??= Array.Empty<string>();
            loaded.NetTariffCodes ??= Array.Empty<string>();
            loaded.HomeAssistantEntities ??= Array.Empty<string>();
            loaded.NetTariffOwner ??= "";
            loaded.Country ??= "DK"; // settings from before countries were supported
            loaded.SectionPins ??= new();
            loaded.SectionLastPins ??= new();
            loaded.SectionWindows ??= new();
            loaded.Plugins ??= new();
            loaded.LaunchItems = (loaded.LaunchItems ?? new()).Where(i => !string.IsNullOrWhiteSpace(i?.Target)).ToList();
            // Before 1.18.1 the settings page saved "C:\" while the widget compares with "C:"
            loaded.HiddenDisks = (loaded.HiddenDisks ?? Array.Empty<string>()).Select(AppSettings.DiskName)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (string.IsNullOrWhiteSpace(loaded.PingTarget)) loaded.PingTarget = "1.1.1.1";
            loaded.SectionPinSummaries ??= Array.Empty<string>();

            settings = loaded;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not read {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Saves atomically: a temporary file is written, flushed to disk and then swapped in for the old one.
    /// If the app is killed while writing, settings.json is either the old or the new version – never half a file.
    /// The previous version is kept as settings.json.bak, which Load falls back to if the main file is damaged anyway.
    /// </summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

            using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(SettingsPath))
            {
                File.Replace(TempPath, SettingsPath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(TempPath, SettingsPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not save settings: {ex.Message}");
            try { File.Delete(TempPath); } catch { /* ignored */ }
        }
    }
}

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LabWidge";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Turns start at sign-in on or off. When the app is installed, start at sign-in always points to the installed
    /// version – even if a development build or an old copy set its own path. Otherwise the running exe is used,
    /// but only if the stored path is gone.
    /// </summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (!enabled)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                return;
            }

            var exePath = Application.ExecutablePath;
            if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var appExe = Path.Combine(AppContext.BaseDirectory, "LabWidge.exe");
                if (File.Exists(appExe)) exePath = appExe;
            }

            var current = key.GetValue(RunValueName) as string;
            var currentExe = current?.Trim('"');
            var installed = File.Exists(SelfInstaller.InstalledExe) ? SelfInstaller.InstalledExe : null;

            string? target = null;
            if (installed != null && !SamePath(currentExe, installed)) target = installed;
            else if (installed == null && (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))) target = exePath;

            if (target != null)
            {
                key.SetValue(RunValueName, '"' + target + '"');
                Logger.Info($"Start at sign-in now points to {target}" + (currentExe != null ? $" (was: {currentExe})." : "."));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not change start at sign-in: {ex.Message}");
        }
    }

    private static bool SamePath(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
