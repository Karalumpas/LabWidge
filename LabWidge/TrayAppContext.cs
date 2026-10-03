using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Net.Http;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class TrayAppContext : ApplicationContext
{
    private const int UpdateIntervalMinutes = 5;
    private const int NotifyTextMaxLength = 127;
    private static CultureInfo Fmt => L.Culture;
    private static readonly HttpClient Http = HttpClientFactory.Create(TimeSpan.FromSeconds(15));

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _widgetItem;
    private readonly ToolStripMenuItem _compactItem;
    private readonly ToolStripMenuItem _dk1Item;
    private readonly ToolStripMenuItem _dk2Item;
    private readonly ToolStripMenuItem _cloudflareItem;
    private readonly System.Windows.Forms.Timer _ipTimer;
    private readonly System.Windows.Forms.Timer _priceTimer;
    private readonly System.Windows.Forms.Timer _homeAssistantTimer;
    private readonly System.Windows.Forms.Timer _cloudflareTimer;
    private readonly System.Windows.Forms.Timer _serviceTimer;
    private readonly System.Windows.Forms.Timer _proxmoxTimer;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly SemaphoreSlim _cloudflareLock = new(1, 1);
    private readonly CloudflareDnsSync _dnsSync = new();
    private readonly SemaphoreSlim _versionCheckLock = new(1, 1);
    private readonly SynchronizationContext? _ui;
    private readonly System.Windows.Forms.Timer _installTimer = new() { Interval = 60_000 };
    private static readonly TimeSpan AutoInstallIdle = TimeSpan.FromMinutes(10);
    private (ReleaseInfo Release, string Path)? _staged;

    private readonly ElectricityPriceService _prices = new(HttpClientFactory.Create(TimeSpan.FromSeconds(20)));
    private readonly SystemMonitor _system = new();
    private readonly AudioService _audio = new();
    private readonly System.Windows.Forms.Timer _audioTimer = new() { Interval = 2000 };
    private readonly NetworkMonitor _network = new();
    private readonly HomeAssistantService _homeAssistant = new(HttpClientFactory.Create(TimeSpan.FromSeconds(10)));
    private readonly CloudflareService _cloudflare = new(HttpClientFactory.Create(TimeSpan.FromSeconds(15)));
    private readonly ServiceMonitor _services = new();
    private readonly ProxmoxService _proxmox = new();
    /// <summary>Last known status per tunnel id, so a change can be reported.</summary>
    private readonly Dictionary<string, string> _tunnelStatus = new();
    private readonly DashboardForm _dashboard;

    private AppSettings _settings;
    private string? _lastIp;
    private string? _lastError;
    private string? _lastCloudflareError;
    private string? _lastToastedCloudflareError;
    private DateTime _lastUpdate = DateTime.MinValue;
    private DateTime _lastSuccessfulIpUpdate = DateTime.MinValue;
    private DateTime _lastCloudflareUpdate = DateTime.MinValue;
    private PriceLevel? _iconLevel;
    private bool _iconInitialized;
    private bool _dialogOpen;

    public TrayAppContext(bool forceSetup = false)
    {
        _settings = SettingsStore.Load();
        // A language chosen in the installer wins; otherwise existing settings keep theirs
        var language = L.TakeInstallerChoice() ?? L.Normalize(_settings.Language) ?? L.Initial(existingUser: _settings.OnboardingCompleted);
        if (_settings.Language != language)
        {
            _settings.Language = language;
            SettingsStore.Save(_settings);
        }
        L.Use(_settings.Language);
        ToastHelper.EnsureToastRegistration();

        // ---------- Menu ----------
        _menu = new ContextMenuStrip();
        _widgetItem = new ToolStripMenuItem(L.T("Show widget", "Vis widget"), null, (_, _) => ToggleWidget()) { Font = new Font(_menu.Font, FontStyle.Bold) };
        _compactItem = new ToolStripMenuItem(L.T("Compact view", "Kompakt visning"), null, (_, _) => _dashboard!.SetCompact(!_settings.CompactMode));
        var copyItem = new ToolStripMenuItem(L.T("Copy external IP", "Kopiér ekstern IP"), null, (_, _) => CopyIp());
        var updateItem = new ToolStripMenuItem(L.T("Refresh now", "Opdatér nu"), null, async (_, _) =>
        {
            await UpdateIpAsync(manual: true);
            await _prices.RefreshAsync(_settings);
        });

        var areaItem = new ToolStripMenuItem(L.T("Price area", "Prisområde"));
        _dk1Item = new ToolStripMenuItem(L.T("DK1 – West Denmark", "DK1 – Vestdanmark"), null, async (_, _) => await SetPriceArea("DK1"));
        _dk2Item = new ToolStripMenuItem(L.T("DK2 – East Denmark", "DK2 – Østdanmark"), null, async (_, _) => await SetPriceArea("DK2"));
        areaItem.DropDownItems.AddRange(new ToolStripItem[] { _dk1Item, _dk2Item });

        _cloudflareItem = new ToolStripMenuItem("Cloudflare");
        _cloudflareItem.DropDownItems.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem(L.T("IP && DNS records...", "IP && DNS-records..."), null, (_, _) => ShowPopup()),
            new ToolStripMenuItem(L.T("Update A records now", "Opdatér A-records nu"), null, async (_, _) => await UpdateCloudflareAsync(force: true)),
            new ToolStripMenuItem(L.T("Open Cloudflare dashboard", "Åbn Cloudflare-dashboard"), null, (_, _) => OpenCloudflare())
        });

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _widgetItem,
            _compactItem,
            new ToolStripSeparator(),
            copyItem,
            updateItem,
            areaItem,
            _cloudflareItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem(L.T("Settings...", "Indstillinger..."), null, (_, _) => OpenSettings()),
            new ToolStripMenuItem(L.T("Setup guide...", "Opsætningsguide..."), null, (_, _) => RunOnboarding()),
            new ToolStripMenuItem(L.T("Check for updates...", "Søg efter opdateringer..."), null, async (_, _) => await CheckForUpdatesAsync(manual: true)),
            new ToolStripSeparator(),
            new ToolStripMenuItem(L.T("Exit", "Afslut"), null, (_, _) => ExitThread())
        });
        _menu.Opening += (_, _) =>
        {
            _widgetItem.Text = _dashboard!.Visible ? L.T("Hide widget", "Skjul widget") : L.T("Show widget", "Vis widget");
            _compactItem.Checked = _settings.CompactMode;
            _dk1Item.Checked = _settings.PriceArea == "DK1";
            _dk2Item.Checked = _settings.PriceArea == "DK2";
            _cloudflareItem.Visible = _settings.HasCloudflare;
            copyItem.Enabled = _lastIp != null;
        };

        // ---------- Tray icon ----------
        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
            Text = L.T("LabWidge is starting…", "LabWidge starter…"),
            Icon = WidgetIcon.CreateIcon(TrayIconSize(), WidgetIcon.Neutral)
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleWidget();
        };

        _dashboard = new DashboardForm(_prices, _system, _network, _audio, _homeAssistant, _cloudflare, _services, _proxmox, _settings, () => SettingsStore.Save(_settings),
            () => _lastIp, BuildCloudflareWidgetStatus, () => UpdateCloudflareAsync(force: true), _menu,
            () => (_lastSuccessfulIpUpdate, _lastError));

        _cloudflare.Updated += () =>
        {
            CheckTunnelChanges();
            ApplyUiState();
        };
        _services.Updated += ApplyUiState;
        _services.Changed += OnServiceChanged;
        // Called from the TLS handshake in the background; saved on the UI thread
        _proxmox.CertificatePinned += () => _ui?.Post(_ => SettingsStore.Save(_settings), null);

        _prices.Updated += () =>
        {
            ApplyUiState();
            CheckPriceAlerts();
        };

        // ---------- Timers ----------
        _ipTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromMinutes(UpdateIntervalMinutes).TotalMilliseconds };
        _ipTimer.Tick += async (_, _) => await UpdateIpAsync(manual: false);
        _ipTimer.Start();

        _priceTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _priceTimer.Tick += async (_, _) =>
        {
            if (_prices.NeedsRefresh(_settings.PriceArea))
            {
                await _prices.RefreshAsync(_settings);
            }
            else
            {
                ApplyUiState(); // icon and tooltip follow the quarter-hour
                CheckPriceAlerts();
            }
        };
        _priceTimer.Start();

        // Home Assistant is polled often, so a light switched on elsewhere shows up quickly
        _homeAssistantTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _homeAssistantTimer.Tick += async (_, _) =>
        {
            var inView = _dashboard.Visible || HomeAssistantPanel.IsOpen;
            if (_settings.HasHomeAssistant && _settings.ShowHomeAssistant && inView)
            {
                await _homeAssistant.RefreshAsync(_settings);
            }
        };
        _homeAssistantTimer.Start();

        // Tunnels are checked even while the widget is hidden, so an outage is reported
        _cloudflareTimer = new System.Windows.Forms.Timer { Interval = 120_000 };
        _cloudflareTimer.Tick += async (_, _) =>
        {
            if (_settings.HasCloudflare) await _cloudflare.RefreshAsync(_settings);
        };
        _cloudflareTimer.Start();

        // Services: the timer is cheap – only addresses that are due are checked (normally every 5 minutes)
        _serviceTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _serviceTimer.Tick += async (_, _) => await CheckServicesAsync();
        _serviceTimer.Start();

        // Proxmox is only fetched while it can be seen – one call every 15 seconds
        _proxmoxTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _proxmoxTimer.Tick += async (_, _) =>
        {
            if (_settings.HasProxmox && ((_dashboard.Visible && _settings.ShowProxmox) || ProxmoxPanel.IsOpen))
                await _proxmox.RefreshAsync(_settings);
        };
        _proxmoxTimer.Start();

        // The first version check runs shortly after start, then once a day
        _updateTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromMinutes(2).TotalMilliseconds };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = (int)TimeSpan.FromHours(24).TotalMilliseconds;
            await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();

        // Audio: the list follows devices being connected, and the default device takes over when the active one disappears
        // The headset battery is read every minute, and at once when an output appears or disappears (e.g. headset switched on)
        _audio.Refresh();
        ApplyStandardAudioAtLogon();
        _ = RefreshBatteriesAsync();
        var batteryTicks = 0;
        _audioTimer.Tick += (_, _) =>
        {
            var before = _audio.Devices;
            if (_audio.Refresh()) FallBackToStandardAudio();
            if (++batteryTicks >= 30 || !before.Select(d => d.Id).SequenceEqual(_audio.Devices.Select(d => d.Id)))
            {
                batteryTicks = 0;
                _ = RefreshBatteriesAsync();
            }
        };
        _audioTimer.Start();

        _installTimer.Tick += (_, _) => InstallStagedWhenIdle();

        // A sleeping laptop can go a long time without a check – so also check when the PC wakes up
        _ui = SynchronizationContext.Current;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        StartupRegistration.Apply(_settings.StartWithWindows);

        _ = UpdateIpAsync(manual: false);
        _ = _prices.RefreshAsync(_settings);
        _ = _homeAssistant.RefreshAsync(_settings);
        _ = _cloudflare.RefreshAsync(_settings);
        if (_settings.WidgetVisible) _ = _proxmox.RefreshAsync(_settings);

        var isUpdate = RecordRunVersion();
        if (_settings.OnboardingCompleted && !forceSetup)
        {
            if (_settings.WidgetVisible) _dashboard.ShowWidget();
            if (isUpdate) RunLater(TimeSpan.FromSeconds(5), ShowWhatsNewToast);
        }
        else
        {
            // Show the guide once the message loop runs
            var once = new System.Windows.Forms.Timer { Interval = 300 };
            once.Tick += (_, _) =>
            {
                once.Stop();
                once.Dispose();
                RunOnboarding();
            };
            once.Start();
        }
    }

    /// <summary>
    /// Shortly after Windows starts, the default device is selected. Not otherwise – so restarting the app
    /// (e.g. for an update) does not take the sound away from the headphones you just chose.
    /// </summary>
    private void ApplyStandardAudioAtLogon()
    {
        if (TimeSpan.FromMilliseconds(Environment.TickCount64) > TimeSpan.FromMinutes(10)) return;
        var standard = _settings.AudioStandardId;
        if (standard == null || standard == _audio.DefaultId || _audio.Devices.All(d => d.Id != standard)) return;
        Logger.Info("Windows just started – selecting the default audio device.");
        _audio.SwitchOutput(standard, _settings);
    }

    /// <summary>The level last warned about per headset: 1 = low, 2 = critical.</summary>
    private readonly Dictionary<string, int> _batteryAlerted = new();

    private async Task RefreshBatteriesAsync()
    {
        await _audio.RefreshBatteriesAsync();
        CheckBatteryAlerts();
    }

    /// <summary>
    /// Warns once at the low and once at the critical level. The warning resets when the headset is charging
    /// or is well above the limit again, so a level hovering around the limit does not cause repeated messages.
    /// </summary>
    private void CheckBatteryAlerts()
    {
        var s = _settings;
        foreach (var (id, battery) in _audio.Batteries)
        {
            var low = Math.Clamp(s.AudioBatteryLowLevel, 1, 99);
            var critical = Math.Clamp(s.AudioBatteryCriticalLevel, 1, low);
            if (battery.Charging || battery.Percent > low + 5)
            {
                _batteryAlerted.Remove(id);
                continue;
            }
            var level = battery.Percent <= critical ? 2 : battery.Percent <= low ? 1 : 0;
            if (level == 0 || _batteryAlerted.GetValueOrDefault(id) >= level) continue;
            _batteryAlerted[id] = level;
            if (!s.AudioBatteryAlert) continue;

            var headset = _audio.Devices.FirstOrDefault(d => d.Id == id);
            if (headset == null) continue;
            var name = s.AudioNames.TryGetValue(id, out var custom) && !string.IsNullOrWhiteSpace(custom) ? custom : headset.Name;
            var buttons = new List<(string, Action)>();
            if (AlternativeOutput(headset) is { } other)
            {
                var otherName = s.AudioNames.TryGetValue(other.Id, out var on) && !string.IsNullOrWhiteSpace(on) ? on : other.Name;
                buttons.Add((L.T($"Switch to {otherName}", $"Skift til {otherName}"), () =>
                {
                    _audio.SwitchOutput(other.Id, _settings);
                    _dashboard.Invalidate();
                }));
            }
            Logger.Info($"Headset battery {battery.Percent} % ({headset.FullName}).");
            ToastHelper.Show("headset-battery",
                level == 2 ? L.T("The headset battery is almost empty", "Headsetbatteriet er næsten tomt") : L.T("The headset battery is low", "Headsetbatteriet er lavt"),
                L.T($"{name}: {battery.Percent} % left", $"{name}: {battery.Percent} % tilbage"), L.T("Put the headset on charge", "Sæt headsettet til opladning"),
                buttons, () => _dashboard.ShowWidget(), silent: level < 2, expireAfter: TimeSpan.FromMinutes(30));
        }
    }

    /// <summary>Where the sound can go when the headset runs out: the default device, otherwise a speaker or monitor.</summary>
    private AudioDevice? AlternativeOutput(AudioDevice headset)
    {
        var candidates = _audio.Devices.Where(d => d.Id != headset.Id && d.ContainerId != headset.ContainerId && !AudioService.HiddenByDefault(d)).ToList();
        return candidates.FirstOrDefault(d => d.Id == _settings.AudioStandardId)
               ?? candidates.FirstOrDefault(d => d.Kind is AudioKind.Speakers or AudioKind.Display)
               ?? candidates.FirstOrDefault();
    }

    /// <summary>The active audio device disappeared (switched off, unplugged): switch to the default device, if present.</summary>
    private void FallBackToStandardAudio()
    {
        var standard = _settings.AudioStandardId;
        if (standard == null || standard == _audio.DefaultId || _audio.Devices.All(d => d.Id != standard)) return;
        Logger.Info("The active audio device disappeared – switching to the default audio device.");
        if (_audio.SwitchOutput(standard, _settings)) _dashboard.Invalidate();
    }

    /// <summary>Stores the running version. True if the app was just updated from an older version.</summary>
    private bool RecordRunVersion()
    {
        var current = UpdateService.Current;
        var previous = UpdateService.ParseVersion(_settings.LastRunVersion);
        if (previous == current) return false;

        var wasInstalled = _settings.OnboardingCompleted; // a new installation has not been through the guide
        _settings.LastRunVersion = current.ToString();
        SettingsStore.Save(_settings);
        // Without a stored version it is an update from before 1.5.2, which did not store it
        return wasInstalled && (previous == null || previous < current);
    }

    private void ShowWhatsNewToast()
    {
        var version = UpdateService.Current;
        var notes = UpdateService.BundledNotes(version);
        var all = notes.Count > 0 ? string.Join("\n", notes.Select(n => "• " + n)) : "";
        void ShowAll() => ShowInfo(L.T($"New in LabWidge {version}", $"Nyt i LabWidge {version}") + "\n\n"
                                   + (all.Length > 0 ? all : L.T("No description of this version.", "Ingen beskrivelse af denne version.")));

        var line1 = notes.Count > 0 ? notes[0] : L.T("The update is installed and your settings are kept.", "Opdateringen er installeret, og dine indstillinger er bevaret.");
        string? line2 = notes.Count switch
        {
            0 or 1 => null,
            2 => notes[1],
            _ => L.T($"+ {notes.Count - 1} more – click to see them", $"+ {notes.Count - 1} andre nyheder – klik for at se dem")
        };
        ToastHelper.Show("whatsnew", L.T($"LabWidge was updated to {version}", $"LabWidge er opdateret til {version}"), line1, line2,
            notes.Count > 1 ? new (string, Action)[] { (L.T("See what's new", "Se hvad der er nyt"), ShowAll) } : Array.Empty<(string, Action)>(),
            ShowAll,
            silent: true,
            expireAfter: TimeSpan.FromDays(1));
        Logger.Info($"Showed \"What's new in {version}\".");
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        // The event arrives on another thread; the network is not ready right after waking up either
        _ui?.Post(_ => RunLater(TimeSpan.FromMinutes(1), async () =>
        {
            if (_settings.LastUpdateCheck is DateTime last && DateTime.Now - last < TimeSpan.FromHours(6)) return;
            await CheckForUpdatesAsync(manual: false);
        }), null);
    }

    /// <summary>Runs an action once on the UI thread after a delay.</summary>
    private static void RunLater(TimeSpan delay, Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = (int)delay.TotalMilliseconds };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            action();
        };
        timer.Start();
    }

    private static void RunLater(TimeSpan delay, Func<Task> action) => RunLater(delay, () => { _ = action(); });

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        HomeAssistantPanel.CloseIfOpen();
        CloudflarePanel.CloseIfOpen();
        ProxmoxPanel.CloseIfOpen();
        _ipTimer.Dispose();
        _priceTimer.Dispose();
        _homeAssistantTimer.Dispose();
        _cloudflareTimer.Dispose();
        _serviceTimer.Dispose();
        _proxmoxTimer.Dispose();
        _updateTimer.Dispose();
        _audioTimer.Dispose();
        _installTimer.Dispose();
        _dashboard.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
        _updateLock.Dispose();
        _cloudflareLock.Dispose();
        _versionCheckLock.Dispose();
        base.ExitThreadCore();
    }

    // ---------- Settings and setup guide ----------

    private void OpenSettings()
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        bool runWizard;
        try
        {
            using var form = new SettingsWindow(_settings);
            var result = form.ShowDialog();
            runWizard = form.RunOnboardingRequested;
            if (result == DialogResult.OK) ApplyNewSettings(form.Settings);
        }
        finally
        {
            _dialogOpen = false;
        }
        if (runWizard) RunOnboarding();
    }

    private void RunOnboarding()
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            using var form = new OnboardingForm(_settings);
            if (form.ShowDialog() == DialogResult.OK)
            {
                form.Settings.WidgetVisible = true;
                ApplyNewSettings(form.Settings);
                _dashboard.ShowWidget();
                ToastHelper.Show("welcome", L.T("LabWidge is ready", "LabWidge er klar"),
                    L.T("Click the bolt by the clock to show or hide the widget", "Klik på lynet ved uret for at vise eller skjule widgetten"),
                    L.T("Right-click the bolt for settings", "Højreklik på lynet for indstillinger"),
                    Array.Empty<(string, Action)>(), ShowWidgetFromToast, expireAfter: TimeSpan.FromMinutes(5));
            }
            else if (!_settings.OnboardingCompleted)
            {
                // The guide was closed the first time: show only the spot price, so the price is not misleading
                if (!_settings.HasNetTariff) _settings.PriceShowTotal = false;
                _settings.OnboardingCompleted = true;
                SettingsStore.Save(_settings);
                _dashboard.ApplySettings(_settings);
                _dashboard.ShowWidget();
            }
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void ApplyNewSettings(AppSettings updated)
    {
        var old = _settings;
        _settings = updated;
        SettingsStore.Save(_settings);

        _dashboard.ApplySettings(_settings);
        StartupRegistration.Apply(_settings.StartWithWindows);

        var priceChanged = old.PriceArea != updated.PriceArea
                           || old.NetTariffOwner != updated.NetTariffOwner
                           || !old.NetTariffCodes.SequenceEqual(updated.NetTariffCodes);
        if (priceChanged) _ = _prices.RefreshAsync(_settings);

        var homeAssistantChanged = old.HomeAssistantEnabled != updated.HomeAssistantEnabled
                                   || old.HomeAssistantUrl != updated.HomeAssistantUrl
                                   || !old.HomeAssistantEntities.SequenceEqual(updated.HomeAssistantEntities);
        if (homeAssistantChanged) _ = _homeAssistant.RefreshAsync(_settings);

        var dnsSettingsChanged = old.HasCloudflare != updated.HasCloudflare
                                 || old.ZoneId != updated.ZoneId
                                 || old.CloudflareAutoUpdate != updated.CloudflareAutoUpdate
                                 || old.UpdateAllARecords != updated.UpdateAllARecords
                                 || !old.IncludedHosts.SequenceEqual(updated.IncludedHosts);
        if (dnsSettingsChanged) _dnsSync.Invalidate();
        if (updated.HasCloudflare && dnsSettingsChanged && _lastIp != null)
            _ = UpdateCloudflareAsync(force: false);

        var cloudflareChanged = old.HasCloudflare != updated.HasCloudflare
                                || old.ZoneId != updated.ZoneId
                                || old.CloudflareAccountId != updated.CloudflareAccountId;
        if (cloudflareChanged)
        {
            _tunnelStatus.Clear();
            _ = _cloudflare.RefreshAsync(_settings);
        }

        var proxmoxChanged = old.HasProxmox != updated.HasProxmox
                             || old.ProxmoxUrl != updated.ProxmoxUrl
                             || old.ProxmoxTokenId != updated.ProxmoxTokenId
                             || old.ProxmoxAllowSelfSigned != updated.ProxmoxAllowSelfSigned;
        if (proxmoxChanged) _ = _proxmox.RefreshAsync(_settings);

        ApplyUiState();
        Logger.Info("Settings saved.");

        if (L.Normalize(old.Language) != L.Normalize(updated.Language)) OfferRestartForLanguage();
    }

    /// <summary>The menus and windows are built in the old language, so a language change takes a restart.</summary>
    private void OfferRestartForLanguage()
    {
        // Asked in the new language, since that is the one the user just chose
        L.Use(_settings.Language);
        var answer = MessageBox.Show(
            L.T("LabWidge needs to restart to switch language. Restart now?",
                "LabWidge skal genstartes for at skifte sprog. Genstart nu?"),
            "LabWidge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes) Program.Restart();
    }

    private async Task SetPriceArea(string area)
    {
        _settings.PriceArea = area;
        SettingsStore.Save(_settings);
        _dashboard.ApplySettings(_settings);
        await _prices.RefreshAsync(_settings);
    }

    private void ToggleWidget()
    {
        _dashboard.ToggleVisible();
        _settings.WidgetVisible = _dashboard.Visible;
        SettingsStore.Save(_settings);

        // The numbers must be fresh the moment the widget appears
        if (_dashboard.Visible)
        {
            _ = _homeAssistant.RefreshAsync(_settings);
            _ = _proxmox.RefreshAsync(_settings);
        }
        else
        {
            HomeAssistantPanel.CloseIfOpen();
            CloudflarePanel.CloseIfOpen();
            ProxmoxPanel.CloseIfOpen();
        }
    }

    private void ShowWidgetFromToast()
    {
        if (!_dashboard.Visible) ToggleWidget();
    }

    // ---------- Icon and tooltip ----------

    private void ApplyUiState()
    {
        _notifyIcon.Text = TrimNotifyText(BuildTooltip());
        UpdateNotifyIcon();
        _dashboard.Invalidate();
    }

    /// <summary>The tray icon's bolt is coloured by the current electricity price (green/yellow/red).</summary>
    private void UpdateNotifyIcon()
    {
        PriceLevel? level = null;
        var cur = _prices.Current(DateTime.Now);
        if (cur != null && _settings.ShowPrice)
        {
            var values = _prices.HourlyConsumer(_settings).Select(h => h.Value).ToList();
            level = ElectricityPriceService.Level(_prices.Consumer(cur.Time, cur.SpotOre, _settings), values);
        }

        if (_iconInitialized && level == _iconLevel) return;

        var color = level is PriceLevel l ? WidgetIcon.LevelColor(l) : WidgetIcon.Amber;
        var old = _notifyIcon.Icon;
        _notifyIcon.Icon = WidgetIcon.CreateIcon(TrayIconSize(), color);
        old?.Dispose();
        _iconLevel = level;
        _iconInitialized = true;
    }

    /// <summary>Several lines: price and level, next cheap period, IP and ping.</summary>
    private string BuildTooltip()
    {
        var lines = new List<string>();
        var now = DateTime.Now;
        var cur = _prices.Current(now);
        if (cur != null && _settings.ShowPrice)
        {
            var hours = _prices.HourlyConsumer(_settings);
            var values = hours.Select(h => h.Value).ToList();
            var price = _prices.Consumer(cur.Time, cur.SpotOre, _settings);
            var level = ElectricityPriceService.Level(price, values) switch
            {
                PriceLevel.Cheap => L.T("cheap", "billig"),
                PriceLevel.Medium => L.T("medium", "middel"),
                _ => L.T("expensive", "dyr")
            };
            lines.Add($"⚡ {price.ToString("0", Fmt)} øre/kWh ({level})");

            var curHour = now.Date.AddHours(now.Hour);
            var future = hours.Where(h => h.Hour >= curHour).ToList();
            if (future.Count >= 3)
            {
                var best = Enumerable.Range(0, future.Count - 2)
                    .OrderBy(i => future[i].Value + future[i + 1].Value + future[i + 2].Value)
                    .First();
                var start = future[best].Hour;
                var day = start.Date == now.Date ? L.T("today", "i dag") : L.T("tomorrow", "i morgen");
                lines.Add(L.T($"Cheapest {day} {start:HH}–{start.AddHours(3):HH}", $"Billigst {day} kl. {start:HH}–{start.AddHours(3):HH}"));
            }
        }

        var ip = _lastError != null
            ? L.T($"IP error (last {_lastIp ?? "unknown"})", $"IP-fejl (sidst {_lastIp ?? "ukendt"})")
            : "IP " + (_lastIp ?? L.T("fetching…", "henter…"));
        if (_network.PingMs is long ping) ip += $" · {ping} ms";
        lines.Add(ip);

        if (_settings.HasCloudflare && _lastCloudflareError != null)
            lines.Add(L.T("⚠ Cloudflare error", "⚠ Cloudflare-fejl"));
        if (_settings.HasCloudflare && _cloudflare.Tunnels.Where(t => t.IsDown).Select(t => t.Name).ToList() is { Count: > 0 } down)
            lines.Add(L.T("⚠ Tunnel down: ", "⚠ Tunnel nede: ") + string.Join(", ", down));
        if (_settings.HasCloudflare && _settings.ServiceChecksEnabled
            && _services.Down(ServiceMonitor.CheckableHosts(_cloudflare.Tunnels, _settings)) is { Count: > 0 } services)
            lines.Add(L.T("⚠ Not responding: ", "⚠ Svarer ikke: ") + string.Join(", ", services.Select(sv => sv.Host.Split('.')[0])));

        return string.Join("\n", lines);
    }

    private static string TrimNotifyText(string text) =>
        text.Length <= NotifyTextMaxLength ? text : text[..(NotifyTextMaxLength - 1)] + "…";

    private static int TrayIconSize()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return (int)Math.Round(16 * g.DpiX / 96f);
    }

    // ---------- Updates ----------

    /// <summary>
    /// Looks for a newer release on GitHub. Nothing is downloaded without the user's consent:
    /// automatic checks show a notification, manual checks a dialog.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (!manual && (!_settings.AutoCheckUpdates || !_settings.OnboardingCompleted)) return;
        if (!await _versionCheckLock.WaitAsync(0)) return;

        try
        {
            var release = await UpdateService.FetchLatestAsync(Http);
            _settings.LastUpdateCheck = DateTime.Now;
            SettingsStore.Save(_settings);

            if (release == null)
            {
                if (manual)
                {
                    ShowInfo(L.T($"No release was found on GitHub.\n\nYou are running version {UpdateService.Current}.",
                                 $"Der blev ikke fundet nogen udgivelse på GitHub.\n\nDu kører version {UpdateService.Current}."));
                }
                return;
            }

            if (!UpdateService.IsNewer(release))
            {
                Logger.Info($"Version check: {UpdateService.Current} is the latest.");
                if (manual) ShowInfo(L.T($"You are running the latest version ({UpdateService.Current}).", $"Du kører den nyeste version ({UpdateService.Current})."));
                return;
            }

            if (!manual && _settings.SkippedUpdateVersion == release.Version.ToString())
            {
                return; // the user chose to skip this version
            }

            Logger.Info($"Version {release.Version} is available.");
            if (manual) PromptForUpdate(release);
            else if (_settings.AutoInstallUpdates) await StageUpdateAsync(release);
            else ShowUpdateToast(release);
        }
        catch (Exception ex)
        {
            Logger.Error($"Version check failed: {ex.Message}");
            if (manual) ShowInfo(L.T("Could not reach GitHub right now:\n\n", "Kunne ikke kontakte GitHub lige nu:\n\n") + ex.Message);
        }
        finally
        {
            _versionCheckLock.Release();
        }
    }

    /// <summary>
    /// Automatic update: download and verify the installer in the background, and only install it once
    /// the PC has not been used for a while – so the app does not restart in the middle of something.
    /// </summary>
    private async Task StageUpdateAsync(ReleaseInfo release)
    {
        if (_staged?.Release.Version == release.Version && File.Exists(_staged.Value.Path)) return;
        try
        {
            var path = await UpdateService.DownloadAsync(release, null, CancellationToken.None);
            if (!AutoInstallEnabled)
            {
                // Turned off while downloading: ask instead
                ShowUpdateToast(release);
                return;
            }
            _staged = (release, path);
            Logger.Info($"Version {release.Version} is downloaded and will be installed when the PC is idle.");
            _installTimer.Start();
        }
        catch (Exception ex)
        {
            // If it cannot be downloaded in the background, the user decides as before
            Logger.Error($"Automatic download of {release.Version} failed: {ex.Message}");
            ShowUpdateToast(release);
        }
    }

    private void InstallStagedWhenIdle()
    {
        if (_staged is not { } staged) return;
        var (release, path) = staged;
        if (!AutoInstallEnabled)
        {
            // The user turned automatic installation off after the update was downloaded
            Logger.Info($"Automatic installation is off – {release.Version} will not install by itself.");
            _staged = null;
            _installTimer.Stop();
            return;
        }
        if (!File.Exists(path))
        {
            _staged = null;
            _installTimer.Stop();
            return;
        }
        // Not in the middle of a dialog or while the user is at the PC
        if (_dialogOpen || HomeAssistantPanel.IsOpen || UserIdle.Duration < AutoInstallIdle) return;

        try
        {
            _installTimer.Stop();
            Logger.Info($"Installing {release.Version} automatically (idle for {UserIdle.Duration.TotalMinutes:0} min).");
            UpdateService.Launch(path);
            ExitThread();
        }
        catch (Exception ex)
        {
            Logger.Error($"Automatic installation of {release.Version} failed: {ex.Message}");
            _staged = null;
            ShowUpdateToast(release);
        }
    }

    private bool AutoInstallEnabled => _settings.AutoCheckUpdates && _settings.AutoInstallUpdates;

    private void ShowUpdateToast(ReleaseInfo release)
    {
        var size = (release.Size / 1048576.0).ToString("0.#", Fmt);
        var headline = release.Size > 0
            ? L.T($"Downloaded from GitHub ({size} MB)", $"Hentes fra GitHub ({size} MB)")
            : L.T("Downloaded from GitHub", "Hentes fra GitHub");
        ToastHelper.Show("update", L.T($"LabWidge {release.Version} is ready", $"LabWidge {release.Version} er klar"),
            headline,
            L.T($"You are running {UpdateService.Current}", $"Du kører {UpdateService.Current}"),
            new (string, Action)[]
            {
                (L.T("Install now", "Installér nu"), () => InstallUpdate(release)),
                (L.T("Skip", "Spring over"), () => SkipVersion(release))
            },
            () => InstallUpdate(release),
            silent: false,
            expireAfter: TimeSpan.FromHours(12));
    }

    private void PromptForUpdate(ReleaseInfo release)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        DialogResult answer;
        try
        {
            var notes = release.Notes.Trim();
            if (notes.Length > 400) notes = notes[..400].TrimEnd() + "…";

            answer = MessageBox.Show(
                L.T($"Version {release.Version} is ready – you are running {UpdateService.Current}.\n\n",
                    $"Version {release.Version} er klar – du kører {UpdateService.Current}.\n\n") +
                (notes.Length > 0 ? notes + "\n\n" : "") +
                L.T("Download and install it now? The app closes and starts again by itself, and your settings are kept.",
                    "Vil du hente og installere den nu? Appen lukker og starter igen af sig selv, og dine indstillinger bevares."),
                L.T("LabWidge – Update", "LabWidge – Opdatering"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        }
        finally
        {
            _dialogOpen = false;
        }

        if (answer == DialogResult.Yes) InstallUpdate(release);
    }

    private void SkipVersion(ReleaseInfo release)
    {
        _settings.SkippedUpdateVersion = release.Version.ToString();
        SettingsStore.Save(_settings);
        Logger.Info($"Skipping version {release.Version}.");
    }

    /// <summary>Downloads the installer and lets it take over. The app closes by itself afterwards.</summary>
    private void InstallUpdate(ReleaseInfo release)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        string? setupPath = null;
        try
        {
            using var form = new UpdateDownloadForm(release);
            if (form.ShowDialog() == DialogResult.OK) setupPath = form.SetupPath;
        }
        finally
        {
            _dialogOpen = false;
        }

        if (setupPath == null) return;

        try
        {
            UpdateService.Launch(setupPath);
            Logger.Info($"The installer for {release.Version} has started.");
            ExitThread();
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not start the installer: {ex.Message}");
            ShowInfo(L.T("The installer could not be started:\n\n", "Installationsprogrammet kunne ikke startes:\n\n") + ex.Message);
        }
    }

    private void ShowInfo(string message)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            MessageBox.Show(message, "LabWidge", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ---------- Price alerts ----------

    private void CheckPriceAlerts()
    {
        if (!_settings.OnboardingCompleted) return;

        foreach (var alert in PriceAlerts.Evaluate(_prices, _settings, DateTime.Now))
        {
            if (alert.Kind == PriceAlertKind.Cheap) _settings.LastCheapAlertFor = alert.StartsAt;
            else _settings.LastExpensiveAlertFor = alert.StartsAt;
            SettingsStore.Save(_settings);

            ToastHelper.Show(alert.Kind == PriceAlertKind.Cheap ? "price-cheap" : "price-expensive",
                alert.Title, alert.Line1, alert.Line2,
                new (string, Action)[] { (L.T("Show widget", "Vis widget"), ShowWidgetFromToast) },
                ShowWidgetFromToast,
                silent: false,
                expireAfter: TimeSpan.FromHours(3));
            Logger.Info($"Price alert: {alert.Title}");
        }
    }

    // ---------- IP ----------

    private async Task UpdateIpAsync(bool manual)
    {
        if (!await _updateLock.WaitAsync(0)) return;

        try
        {
            var ip = await ExternalIpProvider.FetchAsync(Http).ConfigureAwait(true);
            var previous = _lastIp;
            var changed = previous != null && !string.Equals(previous, ip, StringComparison.Ordinal);
            _lastIp = ip;
            _lastError = null;
            _lastUpdate = DateTime.Now;
            _lastSuccessfulIpUpdate = _lastUpdate;
            ApplyUiState();

            if (changed && _settings.NotifyIpChange)
            {
                ToastHelper.Show("ip", L.T("Your external IP address changed", "Din eksterne IP-adresse er ændret"), ip,
                    L.T($"Before: {previous}", $"Før: {previous}"),
                    new (string, Action)[] { (L.T("Copy IP", "Kopiér IP"), CopyIp), (L.T("Show widget", "Vis widget"), ShowWidgetFromToast) },
                    ShowWidgetFromToast, silent: false);
            }
            else if (manual && _settings.NotifyIpChange)
            {
                ToastHelper.Show("ip", L.T("External IP address unchanged", "Ekstern IP-adresse er uændret"), ip,
                    L.T($"Checked at {DateTime.Now:HH:mm}", $"Tjekket kl. {DateTime.Now:HH:mm}"),
                    new (string, Action)[] { (L.T("Copy IP", "Kopiér IP"), CopyIp) },
                    ShowWidgetFromToast, expireAfter: TimeSpan.FromMinutes(2));
            }

            // First lookup after start or a new IP: sync Cloudflare.
            // The sync only remembers the IP after a successful DNS call.
            await UpdateCloudflareAsync(force: false);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _lastUpdate = DateTime.Now;
            ApplyUiState();
            Logger.Error($"IP update failed: {ex}");
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private void CopyIp()
    {
        if (string.IsNullOrWhiteSpace(_lastIp)) return;
        try
        {
            Clipboard.SetText(_lastIp);
            PopupForm.SetStatus(L.T("IP copied.", "IP kopieret."), StatusLevel.Success);
            Logger.Info("IP copied to the clipboard.");
        }
        catch
        {
            PopupForm.SetStatus(L.T("Could not copy the IP.", "Kunne ikke kopiere IP."), StatusLevel.Error);
            Logger.Error("Could not copy the IP to the clipboard.");
        }
    }

    // ---------- Cloudflare ----------

    private void ShowPopup()
    {
        var ip = string.IsNullOrWhiteSpace(_lastIp) ? L.T("unknown", "ukendt") : _lastIp;
        var timestamp = _lastUpdate == DateTime.MinValue ? L.T("unknown", "ukendt") : _lastUpdate.ToString("yyyy-MM-dd HH:mm:ss");
        PopupForm.ShowPopup(ip, timestamp, _settings,
            onCopy: CopyIp,
            onUpdate: async () => await UpdateIpAsync(manual: true),
            onCloudflare: OpenCloudflare,
            onSettings: OpenSettings,
            onUpdateCloudflare: async () => await UpdateCloudflareAsync(force: true));
    }

    private async Task UpdateCloudflareAsync(bool force)
    {
        if (!_settings.HasCloudflare) return;
        if (string.IsNullOrWhiteSpace(_lastIp))
        {
            PopupForm.SetStatus(L.T("No IP yet.", "Ingen IP endnu."), StatusLevel.Warning);
            return;
        }
        if (!_settings.CloudflareAutoUpdate && !force) return;
        if (!force && !_dnsSync.NeedsUpdate(_lastIp)) return;

        var token = CredentialStore.ReadToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            SetCloudflareError(L.T("The Cloudflare token is missing", "Cloudflare-token mangler"));
            return;
        }

        if (!await _cloudflareLock.WaitAsync(0))
        {
            PopupForm.SetStatus(L.T("A Cloudflare update is already running.", "Cloudflare-opdatering kører allerede."), StatusLevel.Info);
            return;
        }

        try
        {
            PopupForm.SetStatus(L.T("Updating Cloudflare A records...", "Opdaterer Cloudflare A-records..."), StatusLevel.Info);
            var ip = _lastIp;
            var zone = _settings.ZoneId!;
            var hosts = _settings.UpdateAllARecords ? null : _settings.IncludedHosts.ToArray();
            var updated = await _dnsSync.SyncAsync(ip, () =>
                CloudflareClient.UpdateAllARecordsAsync(zone, token, ip, hosts));

            _lastCloudflareError = null;
            _lastToastedCloudflareError = null;
            _lastCloudflareUpdate = DateTime.Now;
            PopupForm.SetStatus(updated == 0
                    ? L.T("Cloudflare: no A records changed.", "Cloudflare: ingen A-records ændret.")
                    : L.T($"Cloudflare: updated {updated} A records.", $"Cloudflare: opdateret {updated} A-records."),
                updated == 0 ? StatusLevel.Warning : StatusLevel.Success);
            Logger.Info($"Cloudflare updated: {updated} A records.");
        }
        catch (Exception ex)
        {
            SetCloudflareError(ex.Message);
        }
        finally
        {
            _cloudflareLock.Release();
            ApplyUiState();
        }

        // The widget's DNS line must show the result at once
        await _cloudflare.RefreshAsync(_settings);
    }

    private async Task CheckServicesAsync()
    {
        if (!_settings.HasCloudflare || !_settings.ServiceChecksEnabled || _cloudflare.Tunnels.Count == 0) return;
        await _services.CheckDueAsync(ServiceMonitor.CheckableHosts(_cloudflare.Tunnels, _settings));
    }

    /// <summary>Reports when a service stops responding – and when it responds again.</summary>
    private void OnServiceChanged(ServiceStatus status, bool down)
    {
        // If the tunnel itself is down, it has already reported that
        var tunnel = _cloudflare.Tunnels.FirstOrDefault(t => t.Routes.Any(r => r.Hostname.Equals(status.Host, StringComparison.OrdinalIgnoreCase)));
        if (tunnel is { IsDown: true }) return;

        if (down)
        {
            Logger.Error($"{status.Host} is not responding: {status.Describe()}");
            if (_settings.NotifyServiceDown)
            {
                var route = tunnel?.Routes.FirstOrDefault(r => r.Hostname.Equals(status.Host, StringComparison.OrdinalIgnoreCase));
                ToastHelper.Show("service-" + status.Host, L.T($"{status.Host} is not responding", $"{status.Host} svarer ikke"), status.Describe(),
                    route?.Service != null ? L.T($"Service behind it: {route.Service}", $"Tjenesten bag: {route.Service}") : null,
                    new (string, Action)[] { (L.T("Show widget", "Vis widget"), ShowWidgetFromToast) },
                    ShowWidgetFromToast, silent: false);
            }
        }
        else
        {
            Logger.Info($"{status.Host} responds again.");
            if (_settings.NotifyServiceDown)
            {
                ToastHelper.Show("service-" + status.Host, L.T($"{status.Host} responds again", $"{status.Host} svarer igen"), status.Describe(), null,
                    Array.Empty<(string, Action)>(), ShowWidgetFromToast, expireAfter: TimeSpan.FromMinutes(30));
            }
        }
    }

    /// <summary>Reports when a tunnel goes down – and when it is up again after being reported down.</summary>
    private void CheckTunnelChanges()
    {
        if (!_settings.HasCloudflare || _cloudflare.TunnelError != null) return;

        foreach (var t in _cloudflare.Tunnels)
        {
            var known = _tunnelStatus.TryGetValue(t.Id, out var before);
            _tunnelStatus[t.Id] = t.Status;
            if (!known || before == t.Status) continue; // the first lookup after start reports nothing

            var wasDown = before is "down" or "inactive";
            if (t.IsDown && !wasDown)
            {
                Logger.Error($"Cloudflare Tunnel \"{t.Name}\" is {t.Status}.");
                if (_settings.NotifyTunnelDown)
                {
                    ToastHelper.Show("tunnel-" + t.Id, L.T($"Tunnel \"{t.Name}\" is down", $"Tunnel \"{t.Name}\" er nede"),
                        L.T("Cloudflare cannot reach cloudflared on your network", "Cloudflare kan ikke nå cloudflared på dit netværk"),
                        t.Routes.Count > 0 ? string.Join(", ", t.Routes.Take(3).Select(r => r.Hostname)) + L.T(" not responding", " svarer ikke") : null,
                        new (string, Action)[] { (L.T("Show widget", "Vis widget"), ShowWidgetFromToast) },
                        ShowWidgetFromToast, silent: false);
                }
            }
            else if (wasDown && !t.IsDown)
            {
                Logger.Info($"Cloudflare Tunnel \"{t.Name}\" is {t.Status} again.");
                if (_settings.NotifyTunnelDown)
                {
                    ToastHelper.Show("tunnel-" + t.Id, L.T($"Tunnel \"{t.Name}\" is up again", $"Tunnel \"{t.Name}\" er oppe igen"),
                        L.T($"{t.Connections} connections", $"{t.Connections} forbindelser"), null,
                        Array.Empty<(string, Action)>(), ShowWidgetFromToast, expireAfter: TimeSpan.FromMinutes(30));
                }
            }
        }
    }

    private void SetCloudflareError(string message)
    {
        _lastCloudflareError = message;
        _lastCloudflareUpdate = DateTime.Now;
        PopupForm.SetStatus(L.T("Cloudflare error. See the log.", "Cloudflare-fejl. Se log."), StatusLevel.Error);
        Logger.Error($"Cloudflare update failed: {message}");

        if (_settings.NotifyCloudflareError && _lastToastedCloudflareError != message)
        {
            _lastToastedCloudflareError = message;
            ToastHelper.Show("cloudflare", L.T("Cloudflare could not be updated", "Cloudflare kunne ikke opdateres"), message,
                L.T("Your A records may point to an old IP", "Dine A-records peger måske på en gammel IP"),
                new (string, Action)[] { (L.T("Try again", "Prøv igen"), () => _ = UpdateCloudflareAsync(force: true)), (L.T("Settings", "Indstillinger"), OpenSettings) },
                ShowPopup, silent: false);
        }
    }

    private string? BuildCloudflareWidgetStatus()
    {
        if (!_settings.HasCloudflare) return null;
        if (!_settings.CloudflareAutoUpdate) return L.T("auto-update off", "auto-opdatering slået fra");
        if (_lastCloudflareUpdate == DateTime.MinValue) return L.T("no update yet", "ingen opdatering endnu");

        var when = _lastCloudflareUpdate.ToString("HH:mm");
        return _lastCloudflareError == null
            ? L.T($"updated at {when}", $"opdateret kl. {when}")
            : L.T($"error: {_lastCloudflareError} ({when})", $"fejl: {_lastCloudflareError} ({when})");
    }

    private static void OpenCloudflare()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "https://dash.cloudflare.com/", UseShellExecute = true });
        }
        catch
        {
            Logger.Error("Could not open the Cloudflare dashboard.");
        }
    }
}
