using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using LabWidgeSetup;

// A separate process inherits the installation folder, like Setup started by an older widget.
if (args.Length == 3 && args[0] is "--cwd-held" or "--cwd-released")
{
    if (args[0] == "--cwd-released") InstallerEnvironment.Prepare();
    File.WriteAllText(args[1] + ".tmp", Environment.CurrentDirectory);
    File.Move(args[1] + ".tmp", args[1]);
    while (!File.Exists(args[2])) await Task.Delay(50);
    return;
}

// "--live" fetches every price area from the real sources – run it by hand when a source may have changed.
if (args.Length == 1 && args[0] == "--live")
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("LabWidge-tests");
    var failed = 0;
    foreach (var country in Countries.All)
        foreach (var area in country.Areas)
        {
            try
            {
                var prices = await SpotPriceSources.FetchAsync(http, area, country.Unit.Currency, DateTime.Today);
                var first = prices.FirstOrDefault();
                Console.WriteLine($"{country.Code} {area.Code,-6} {prices.Count,4} prices, first {first.StartUtc.ToLocalTime():dd/MM HH:mm} "
                                  + $"{first.PerKwh * country.Unit.PerMajor:0.0} {country.Unit.PerKwh}");
                if (prices.Count == 0) failed++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"{country.Code} {area.Code,-6} FAILED: {ex.Message}");
            }
        }
    Environment.Exit(failed);
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("Cloudflare: private and tunnel DNS records are fetched without writes", ReadOnlyDns),
    ("Cloudflare: all API writes are blocked before transport", BlockCloudflareWrites),
    ("Cloudflare: legacy DNS automation settings cannot survive migration", LegacyCloudflareSettings),
    ("Prices: concurrent settings changes are coalesced and applied", ChangedTariff),
    ("Prices: each source's format is parsed into UTC and price per kWh", SourceFormats),
    ("Prices: OMIE periods count quarter-hours from Spanish midnight", OmieQuarterHours),
    ("Countries: the catalog is complete and Other hides the price", CountryCatalog),
    ("Prices: other countries use their own source, unit and VAT", CountryPrices),
    ("Prices: ENTSO-E countries ask the Worker for their zone and currency", WorkerPrices),
    ("Installer: a complete directory replaces the previous version and keeps login", InstallSuccess),
    ("Installer: a failed switch restores all original files", InstallRollback),
    ("Installer: rollback keeps the prepared package available for retry", InstallRetry),
    ("Installer: interrupted switches recover the old version or finish the new one", InstallRecovery),
    ("Installer: incomplete packages leave the existing version intact", InvalidPackage),
    ("Uninstall: cleanup waits for process exit and handles quoted paths", UninstallWait),
    ("Installer: an inherited working directory is released before switching folders", InstallerWorkingDirectory),
    ("Widget: pinned bands reserve the middle viewport and clamp scroll", LayoutBands),
    ("Widget: status distinguishes fresh, stale and failed data", FreshnessStates),
    ("Widget: size and pin preferences survive settings cloning", LayoutSettings),
    ("Plugins: legacy activation migrates and configuration survives disable and clone", PluginSettings),
    ("Plugins: cancellation stops price requests before tariff fetching and allows restart", CancelPriceFetch),
    ("Plugins: cancelling an old price fetch keeps a newer request waiting behind it", CancelKeepsNewerPriceRequest),
    ("Settings: hidden disks match the widget's disk names", HiddenDiskNames),
    ("Shortcuts: websites, programs and other addresses are told apart and survive cloning", LaunchItems),
    ("Audio: settings follow a device that Windows gives a new id", AudioDeviceNewId),
    ("Prices: cheap and expensive stretches are found in quarter-hours", QuarterWindows),
    ("Prices: alerts start at the quarter-hour the stretch starts", QuarterPriceAlerts),
    ("Appliances: the cheapest start keeps the deadline and moves with cheaper prices", AppliancePlans),
    ("Automation: price rules apply in their hours and act only on change", AutomationRuleChecks),
    ("History: whole days are kept per area with savings and CSV", PriceHistoryDays),
    ("Appliances: a planned run switches on, survives edits and outages, and switches off once", PlannedRunFlow),
    ("Appliances: failed starts, changed addresses, switched-off plugins and late starts are safe", PlannedRunSafety),
    ("Automation: rules switch on change, put back their old entity when edited, off or out of prices", RuleFlow),
    ("Store package: the manifest matches the startup task, logos and values the app and build script use", StorePackageManifest)
};
foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine("PASS " + test.Name);
}
Console.WriteLine($"{tests.Length} regression tests passed.");

static Task StorePackageManifest()
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var template = File.ReadAllText(Path.Combine(root, "Packaging", "AppxManifest.xml"));
    var script = File.ReadAllText(Path.Combine(root, "scripts", "Build-Msix.ps1"));
    // The build script fills in exactly these values; any other brace would reach the Store unfilled
    var values = System.Text.RegularExpressions.Regex.Matches(template, @"\{([A-Za-z]+)\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(v => v).ToArray();
    Check(values.SequenceEqual(new[] { "DisplayName", "IdentityName", "Publisher", "PublisherDisplayName", "Version" }), $"Unexpected manifest values: {string.Join(", ", values)}");
    foreach (var value in values) Check(script.Contains($"{value} = $"), $"Build-Msix.ps1 does not fill in {value}.");

    var xml = System.Xml.Linq.XDocument.Parse(template.Replace("{Version}", "1.0.0.0"));
    var tasks = xml.Descendants().Where(e => e.Name.LocalName == "StartupTask").ToList();
    Check(tasks.Count == 1 && (string?)tasks[0].Attribute("TaskId") == AppPackage.StartupTaskId, "The manifest's startup task does not match AppPackage.StartupTaskId.");
    Check((string?)tasks[0].Attribute("Enabled") == "false", "Start at sign-in must follow the app's setting, not be on before the app has run.");

    // Every logo the manifest and the toasts point to is one the build script writes
    var logos = xml.Descendants().SelectMany(e => e.Attributes().Where(a => a.Value.StartsWith("PackageAssets\\")).Select(a => a.Value))
        .Concat(xml.Descendants().Where(e => e.Name.LocalName == "Logo").Select(e => e.Value))
        .Append(AppPackage.ToastLogoUri.Replace("ms-appx:///", "").Replace('/', '\\'))
        .Distinct().ToList();
    Check(logos.Count >= 3, "The manifest has fewer logos than expected.");
    foreach (var logo in logos)
        Check(script.Contains($"Join-Path $assets '{Path.GetFileName(logo)}'"), $"Build-Msix.ps1 does not write {logo}.");

    Check(!AppPackage.IsPackaged, "The tests should run as an ordinary app.");
    return Task.CompletedTask;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static Task AudioDeviceNewId()
{
    var headset = new AudioDevice("old-hs", "HS80", "Headset (CORSAIR HS80)", AudioKind.Headset, null);
    var monitor = new AudioDevice("monitor", "C49", "C49 (NVIDIA)", AudioKind.Display, null);
    var otherMonitor = new AudioDevice("monitor-2", "C49", "C49 (NVIDIA)", AudioKind.Display, null);
    var mic = new AudioDevice("old-mic", "HS80", "Microphone (CORSAIR HS80)", AudioKind.Headset, null);
    var s = new AppSettings
    {
        AudioDeviceIds = new[] { "monitor", "old-hs" },
        AudioNames = new() { ["old-hs"] = "Headset" },
        AudioMicPairs = new() { ["old-hs"] = "old-mic", ["monitor"] = "" },
        AudioStandardId = "old-hs"
    };
    Check(AudioDeviceMemory.Update(s, new[] { headset, monitor }, new[] { mic }), "Fingerprints were not recorded.");
    Check(s.AudioDeviceKeys.Count == 3 && s.AudioDeviceKeys["old-hs"] == headset.Fingerprint, "Wrong fingerprints recorded.");
    Check(!AudioDeviceMemory.Update(s, new[] { headset, monitor }, new[] { mic }), "Unchanged devices changed the settings.");

    // Switched off: nothing to follow, the settings are kept
    Check(!AudioDeviceMemory.Update(s, new[] { monitor }, Array.Empty<AudioDevice>()), "A disconnected device changed the settings.");
    Check(s.AudioDeviceIds!.SequenceEqual(new[] { "monitor", "old-hs" }), "A disconnected device was forgotten.");

    // Back under new ids – and a second, identical monitor that must not take the first one's place
    var newHeadset = headset with { Id = "new-hs" };
    var newMic = mic with { Id = "new-mic" };
    Check(AudioDeviceMemory.Update(s, new[] { otherMonitor, monitor, newHeadset }, new[] { newMic }), "The new id was not followed.");
    Check(s.AudioDeviceIds!.SequenceEqual(new[] { "monitor", "new-hs" }), "The widget order lost the device.");
    Check(s.AudioNames.Count == 1 && s.AudioNames["new-hs"] == "Headset", "The custom name did not follow.");
    Check(s.AudioStandardId == "new-hs", "The default device did not follow.");
    Check(s.AudioMicPairs["new-hs"] == "new-mic" && s.AudioMicPairs["monitor"] == "" && s.AudioMicPairs.Count == 2, "The microphone pair did not follow.");
    Check(!s.AudioDeviceKeys.ContainsKey("old-hs") && !s.AudioDeviceKeys.ContainsKey("old-mic") && s.AudioDeviceKeys.Count == 3, "Old fingerprints were kept.");

    // An identical device that is not chosen must never take the chosen one's place when it is unplugged
    s.AudioDeviceIds = new[] { "monitor" };
    s.AudioNames.Clear();
    s.AudioMicPairs.Clear();
    s.AudioStandardId = null;
    AudioDeviceMemory.Update(s, new[] { monitor, otherMonitor }, Array.Empty<AudioDevice>());
    Check(!AudioDeviceMemory.Update(s, new[] { otherMonitor }, Array.Empty<AudioDevice>()) && s.AudioDeviceIds.SequenceEqual(new[] { "monitor" }),
        "An identical, unchosen device took over the chosen device's place.");
    // ... not even after the twin has been away for a while
    AudioDeviceMemory.Update(s, new[] { monitor }, Array.Empty<AudioDevice>());
    Check(!AudioDeviceMemory.Update(s, new[] { otherMonitor }, Array.Empty<AudioDevice>()) && s.AudioDeviceIds.SequenceEqual(new[] { "monitor" }),
        "A device seen next to an identical device was followed later.");

    // Two chosen identical devices recorded before twins were tracked: when one is missing, both become twins
    s.AudioDeviceIds = new[] { "monitor", "monitor-2" };
    s.AudioDeviceKeys = new() { ["monitor"] = monitor.Fingerprint, ["monitor-2"] = otherMonitor.Fingerprint };
    AudioDeviceMemory.Update(s, new[] { otherMonitor }, Array.Empty<AudioDevice>());
    Check(!AudioDeviceMemory.Update(s, new[] { monitor with { Id = "monitor-3" } }, Array.Empty<AudioDevice>())
          && s.AudioDeviceIds.SequenceEqual(new[] { "monitor", "monitor-2" }), "The chosen twin was followed to a new id.");

    // Two chosen identical devices both missing: one coming back cannot be told apart
    s.AudioDeviceIds = new[] { "monitor", "monitor-2" };
    s.AudioDeviceKeys = new() { ["monitor"] = monitor.Fingerprint, ["monitor-2"] = otherMonitor.Fingerprint };
    AudioDeviceMemory.Update(s, new[] { monitor with { Id = "monitor-3" } }, Array.Empty<AudioDevice>());
    Check(s.AudioDeviceIds.SequenceEqual(new[] { "monitor", "monitor-2" }), "One of two missing twins was followed to a new id.");

    // A device chosen while its known twin is away is a twin too
    s.AudioDeviceIds = new[] { "monitor" };
    s.AudioDeviceKeys = new();
    AudioDeviceMemory.Update(s, new[] { monitor, otherMonitor }, Array.Empty<AudioDevice>());
    s.AudioDeviceIds = new[] { "monitor", "monitor-2" };
    AudioDeviceMemory.Update(s, new[] { otherMonitor }, Array.Empty<AudioDevice>());
    AudioDeviceMemory.Update(s, new[] { monitor with { Id = "monitor-3" } }, Array.Empty<AudioDevice>());
    Check(s.AudioDeviceIds.SequenceEqual(new[] { "monitor", "monitor-2" }), "A twin chosen later was followed to a new id.");

    // A twin removed from the settings while both are away still shows that the other one cannot be followed
    s.AudioDeviceIds = new[] { "monitor" };
    s.AudioDeviceKeys = new() { ["monitor"] = monitor.Fingerprint, ["monitor-2"] = otherMonitor.Fingerprint };
    AudioDeviceMemory.Update(s, Array.Empty<AudioDevice>(), Array.Empty<AudioDevice>());
    AudioDeviceMemory.Update(s, new[] { otherMonitor }, Array.Empty<AudioDevice>());
    Check(s.AudioDeviceIds.SequenceEqual(new[] { "monitor" }), "A removed twin took over the remaining device's place.");

    // Two possible new devices: it cannot be told which one it is, so the settings are kept
    var speaker = new AudioDevice("speaker", "Desk", "Speakers (USB Audio)", AudioKind.Speakers, null);
    s.AudioDeviceIds = new[] { "speaker" };
    AudioDeviceMemory.Update(s, new[] { speaker }, Array.Empty<AudioDevice>());
    Check(AudioDeviceMemory.Update(s, new[] { speaker with { Id = "a" }, speaker with { Id = "b" } }, Array.Empty<AudioDevice>())
          && s.AudioDeviceIds.SequenceEqual(new[] { "speaker" }), "An ambiguous match was followed.");
    Check(!AudioDeviceMemory.Update(s, new[] { speaker with { Id = "a" } }, Array.Empty<AudioDevice>())
          && s.AudioDeviceIds.SequenceEqual(new[] { "speaker" }), "A device left alone after an ambiguous match was followed.");
    Check(s.Clone().AudioDeviceKeys.Count == s.AudioDeviceKeys.Count, "Fingerprints did not survive cloning.");
    return Task.CompletedTask;
}

static Task LayoutBands()
{
    var layout = WidgetLayout.Calculate(600, 80, 120, 1000, 14);
    Check(layout.MiddleTop == 94 && layout.MiddleHeight == 372 && layout.BottomTop == 466,
        "Pinned areas did not reserve the correct viewport.");
    Check(layout.ScrollMax == 628, "Scroll range included the fixed sections.");
    var scaled = WidgetLayout.Calculate(900, 120, 180, 1500, 21);
    Check(scaled.MiddleHeight == layout.MiddleHeight * 1.5f, "Layout did not scale with DPI.");
    Check(WidgetLayout.Calculate(200, 0, 0, 30, 14).ScrollMax == 0, "Short content should not scroll.");
    var extreme = WidgetLayout.Calculate(100, 200, 200, 30, 14);
    Check(extreme.MiddleHeight >= 0 && extreme.BottomTop >= extreme.MiddleTop, "Tiny viewport produced overlapping bands.");
    return Task.CompletedTask;
}

static Task FreshnessStates()
{
    var now = new DateTime(2026, 10, 2, 18, 30, 0);
    Check(DataFreshness.Describe(DateTime.MinValue, null, TimeSpan.FromMinutes(5), now).Health == DataHealth.Loading, "Missing data was treated as fresh.");
    Check(DataFreshness.Describe(now.AddMinutes(-1), null, TimeSpan.FromMinutes(5), now).Health == DataHealth.Current, "Recent data was treated as stale.");
    Check(DataFreshness.Describe(now.AddMinutes(-10), null, TimeSpan.FromMinutes(5), now).Health == DataHealth.Stale, "Old data lacked a warning.");
    var offline = DataFreshness.Describe(now.AddMinutes(-1), "HTTP 503", TimeSpan.FromMinutes(5), now);
    Check(offline.Health == DataHealth.Offline && offline.Detail.Contains("18:29"), "Failure lost the timestamp of preserved data.");
    return Task.CompletedTask;
}

static Task PluginSettings()
{
    var settings = JsonSerializer.Deserialize<AppSettings>("""{"ShowSystem":false,"ShowAudio":false,"ShowNetwork":true,"HomeAssistantEnabled":true,"HomeAssistantUrl":"http://ha.example.test","HomeAssistantEntities":["light.room"],"CloudflareEnabled":true,"ZoneId":"zone","ShowCloudflare":false} """)!;
    Check(!settings.IsPluginEnabled("system") && !settings.IsPluginEnabled("audio") && settings.IsPluginEnabled("network"), "Legacy local section choices changed.");
    Check(settings.IsPluginEnabled("ha") && settings.HasCloudflare, "Legacy integration choices changed.");
    settings.SetPluginEnabled("cloudflare", false);
    settings.SetPluginEnabled("ha", false);
    settings.Plugins["future-plugin"] = true;
    var copy = settings.Clone();
    Check(!copy.HasCloudflare && !copy.HasHomeAssistant && copy.ZoneId == "zone" && copy.HomeAssistantEntities.Single() == "light.room", "Disabling discarded integration configuration.");
    copy.SetPluginEnabled("cloudflare", true);
    Check(copy.HasCloudflare && !settings.HasCloudflare && copy.Plugins["future-plugin"], "Plugin cloning or reactivation lost preferences.");
    return Task.CompletedTask;
}

static async Task CancelPriceFetch()
{
    using var handler = new CancelHandler();
    using var http = new HttpClient(handler);
    var service = new ElectricityPriceService(http);
    var settings = new AppSettings { Country = "DK" };
    var updates = 0;
    service.Updated += () => updates++;
    using var cancel = new CancellationTokenSource();
    var running = service.RefreshAsync(settings, cancel.Token);
    await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancel.Cancel();
    try { await running.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Canceled fetch completed normally."); }
    catch (OperationCanceledException) { }
    Check(handler.Requests == 1 && updates == 0 && service.LastError == null && service.TariffError == null,
        "Cancellation started tariff requests, published data or recorded an outage.");
    handler.Block = false;
    await service.RefreshAsync(settings).WaitAsync(TimeSpan.FromSeconds(5));
    Check(updates == 1 && service.LastSuccessfulFetch != DateTime.MinValue, "Canceled service could not be restarted.");
}

static async Task CancelKeepsNewerPriceRequest()
{
    using var handler = new CancelHandler();
    using var http = new HttpClient(handler);
    var service = new ElectricityPriceService(http);
    var settings = new AppSettings { Country = "DK" };
    using var oldGeneration = new CancellationTokenSource();
    var running = service.RefreshAsync(settings, oldGeneration.Token);
    await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    // A new plugin generation asks for prices while the old fetch still holds the lock
    await service.RefreshAsync(settings, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    handler.Block = false;
    oldGeneration.Cancel();
    try { await running.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Canceled fetch completed normally."); }
    catch (OperationCanceledException) { }
    Check(service.Prices.Count > 0 && service.LastSuccessfulFetch != DateTime.MinValue, "Cancelling the old fetch dropped the newer request.");
}

static Task HiddenDiskNames()
{
    Check(AppSettings.DiskName(@"C:\") == "C:" && AppSettings.DiskName("D:") == "D:", "Drive names were not normalized like the widget's.");
    return Task.CompletedTask;
}

static Task LaunchItems()
{
    Check(LaunchItem.WebUri("youtube.com")?.ToString() == "https://youtube.com/", "A bare domain was not opened as a website.");
    Check(LaunchItem.WebUri("www.chatgpt.com/c/new")?.Host == "www.chatgpt.com", "A domain with a path was not a website.");
    Check(LaunchItem.WebUri("http://192.168.0.10:8123") != null, "An http address was not a website.");
    foreach (var program in new[] { "notepad.exe", @"C:\Program Files\App\app.exe", "discord://", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Discord.lnk", "" })
        Check(LaunchItem.WebUri(program) == null, $"{program} was taken for a website.");
    Check(new LaunchItem { Target = "https://www.instagram.com" }.DisplayName == "instagram.com", "A website without a name was not named after its host.");
    Check(new LaunchItem { Target = @"C:\Tools\Notes.lnk" }.DisplayName == "Notes", "A shortcut without a name was not named after its file.");

    var settings = new AppSettings { LaunchItems = LaunchItem.Examples() };
    var copy = settings.Clone();
    Check(copy.LaunchItems.Count == 4 && copy.LaunchItems[0].Name == "YouTube" && copy.ShowLaunchRail, "Cloning lost the shortcuts.");
    copy.LaunchItems[0].Name = "Changed";
    Check(settings.LaunchItems[0].Name == "YouTube", "Cloned shortcuts were not independent.");
    return Task.CompletedTask;
}

static Task LayoutSettings()
{
    var settings = new AppSettings { WidgetWidth = 480, WidgetHeight = 600,
        SectionPins = new() { ["price"] = SectionPin.Top, ["audio"] = SectionPin.Bottom },
        SectionPinSummaries = new[] { "price" } };
    var copy = settings.Clone();
    Check(copy.WidgetWidth == 480 && copy.WidgetHeight == 600 && copy.SectionPins["audio"] == SectionPin.Bottom
        && copy.SectionPinSummaries.Contains("price"), "Cloning lost layout preferences.");
    copy.SectionPins.Clear();
    Check(settings.SectionPins.Count == 2, "Cloned layout preferences were not independent.");
    return Task.CompletedTask;
}

static async Task InstallerWorkingDirectory()
{
    var root = Path.Combine(Path.GetTempPath(), "labwidge-test-" + Guid.NewGuid().ToString("N"));
    var app = Path.Combine(root, "app");
    Directory.CreateDirectory(app);
    try
    {
        foreach (var mode in new[] { "--cwd-held", "--cwd-released" })
        {
            var ready = Path.Combine(root, "ready");
            var finish = Path.Combine(root, "finish");
            File.Delete(ready); File.Delete(finish);
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = app, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(typeof(Handler).Assembly.Location);
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add(ready);
            start.ArgumentList.Add(finish);
            using var child = Process.Start(start)!;
            try
            {
                var timeout = Stopwatch.StartNew();
                while (!File.Exists(ready) && !child.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
                Check(File.Exists(ready), "Working-directory probe did not start.");
                if (mode == "--cwd-held")
                {
                    try { Directory.Move(app, app + ".backup"); throw new Exception("Expected the inherited directory to be locked."); }
                    catch (IOException) { }
                }
                else
                {
                    Check(!File.ReadAllText(ready).Equals(app, StringComparison.OrdinalIgnoreCase), "Setup retained the inherited working directory.");
                    Directory.Move(app, app + ".backup");
                    Directory.Move(app + ".backup", app);
                }
            }
            finally
            {
                File.WriteAllText(finish, "done");
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            Check(child.ExitCode == 0, "Working-directory probe failed.");
        }
    }
    finally { Directory.Delete(root, true); }
}

static async Task ReadOnlyDns()
{
    var requests = 0;
    using var http = new HttpClient(new CloudflareReadOnlyHandler(new Handler(request =>
    {
        Check(request.Method == HttpMethod.Get && request.Content == null, "DNS refresh attempted to write.");
        Check(request.Headers.Authorization?.Parameter == "test", "DNS authentication missing.");
        requests++;
        return Task.FromResult(Json(new { success = true, result = new[]
        {
            new { id = "private", type = "A", name = "seer.example.test", content = "192.168.0.25", ttl = 60, proxied = false },
            new { id = "vpn", type = "A", name = "vpn.example.test", content = "203.0.113.1", ttl = 60, proxied = false },
            new { id = "tunnel", type = "CNAME", name = "cloud.example.test", content = "id.cfargotunnel.com", ttl = 1, proxied = true }
        } }));
    })));
    for (var i = 0; i < 2; i++)
    {
        var records = await CloudflareClient.GetRecordsAsync("zone", "test", "A,CNAME", http);
        Check(records.Length == 3 && records[0].Content == "192.168.0.25"
              && records[1].Content == "203.0.113.1" && records[2].Content == "id.cfargotunnel.com",
              "Refresh lost the configured private, VPN or tunnel destination.");
    }
    Check(requests == 2, "Refresh made extra requests.");
}

static async Task BlockCloudflareWrites()
{
    var calls = 0;
    using var http = new HttpClient(new CloudflareReadOnlyHandler(new Handler(_ =>
    {
        calls++;
        return Task.FromResult(Json(new { success = true }));
    })));
    foreach (var method in new[] { HttpMethod.Put, HttpMethod.Post, HttpMethod.Patch, HttpMethod.Delete })
    {
        using var request = new HttpRequestMessage(method, "https://API.CLOUDFLARE.COM/client/v4/zones/zone/dns_records/record");
        try
        {
            await http.SendAsync(request);
            throw new Exception("A Cloudflare write was accepted.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "Cloudflare monitoring is read-only.") { }
    }
    Check(calls == 0, "A blocked write reached the transport.");
    using var read = await http.GetAsync("https://api.cloudflare.com/client/v4/zones/zone/dns_records");
    using var otherService = await http.PostAsync("http://homeassistant.example.test/api/services/light/toggle", null);
    Check(calls == 2, "Read-only guard blocked monitoring or another integration.");
}

static Task LegacyCloudflareSettings()
{
    var old = JsonSerializer.Deserialize<AppSettings>("""{"CloudflareEnabled":true,"ZoneId":"zone","CloudflareAutoUpdate":true,"UpdateAllARecords":true,"IncludedHosts":["vpn.example.test"]}""")!;
    Check(old.HasCloudflare && old.ZoneId == "zone", "Migration discarded the monitoring connection.");
    var saved = JsonSerializer.Serialize(old);
    Check(!saved.Contains("CloudflareAutoUpdate") && !saved.Contains("UpdateAllARecords") && !saved.Contains("IncludedHosts"),
          "Legacy DNS automation remained in the saved settings.");
    return Task.CompletedTask;
}

static async Task ChangedTariff()
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var requests = new List<string>();
    var first = true;
    using var http = new HttpClient(new Handler(async request =>
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
        requests.Add(url);
        if (url.Contains("DayAheadPrices"))
        {
            if (first) { first = false; entered.SetResult(); await resume.Task; }
            return Json(new { records = new[] { new { TimeUTC = DateTime.Today.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss"), DayAheadPriceDKK = 1000 } } });
        }
        return Json(new { records = new[] { new
        {
            ChargeTypeCode = "C", Note = "", ValidFrom = "2020-01-01T00:00:00", ValidTo = (string?)null,
            Price1 = url.Contains("New") ? 1.0 : 0.5
        } } });
    }));
    var service = new ElectricityPriceService(http);
    var settings = new AppSettings { Country = "DK", PriceArea = "DK1", NetTariffOwner = "Old", NetTariffCodes = new[] { "C" } };
    var running = service.RefreshAsync(settings);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    // Mutating the original object must not mix the old area with the new tariff.
    settings.PriceArea = "DK2";
    settings.NetTariffOwner = "New";
    for (var i = 0; i < 5; i++) await service.RefreshAsync(settings);
    resume.SetResult();
    await running;
    Check(service.FetchedArea == "DK:DK2", "Area change was lost.");
    Check(service.NetTariffs.Single().HourlyOre[0] == 100, "Tariff change was lost.");
    Check(requests.Count(r => r.Contains("DayAheadPrices")) == 2, "Repeated refreshes were not coalesced.");
    Check(requests.First(r => r.Contains("ChargeOwner")).Contains("Old"), "In-flight settings were not snapshotted.");
}

static Task SourceFormats()
{
    var utc = (string s) => DateTime.SpecifyKind(DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    var eds = SpotPriceSources.ParseEnergiDataService(
        """{"records":[{"TimeUTC":"2026-10-03T10:00:00","TimeDK":"2026-10-03T12:00:00","PriceArea":"DK1","DayAheadPriceEUR":100,"DayAheadPriceDKK":746}]}""");
    Check(eds.Single() == new SpotSample(utc("2026-10-03T10:00:00"), 0.746), "Energi Data Service: UTC time or DKK/MWh → DKK/kWh is wrong.");

    var se = SpotPriceSources.ParseNordicDaily(
        """[{"SEK_per_kWh":0.80081,"EUR_per_kWh":0.07087,"EXR":11.3,"time_start":"2026-10-03T00:00:00+02:00","time_end":"2026-10-03T00:15:00+02:00"}]""", "SEK_per_kWh");
    Check(se.Single() == new SpotSample(utc("2026-10-02T22:00:00"), 0.80081), "elprisetjustnu: the offset or the SEK price is wrong.");

    var no = SpotPriceSources.ParseNordicDaily(
        """[{"NOK_per_kWh":1.51913,"EUR_per_kWh":0.13969,"EXR":10.875,"time_start":"2026-10-03T00:00:00+02:00","time_end":"2026-10-03T01:00:00+02:00"}]""", "NOK_per_kWh");
    Check(no.Single().PerKwh == 1.51913, "hvakosterstrommen: the NOK price is wrong.");

    var elering = """{"success":true,"data":{"ee":[{"timestamp":1790985600,"price":31.37}],"fi":[{"timestamp":1790985600,"price":20}]}}""";
    Check(SpotPriceSources.ParseElering(elering, "fi").Single() == new SpotSample(DateTimeOffset.FromUnixTimeSeconds(1790985600).UtcDateTime, 0.02),
          "Elering: the zone or EUR/MWh → EUR/kWh is wrong.");
    Check(SpotPriceSources.ParseElering(elering, "lv").Count == 0, "Elering: a missing zone must give no prices.");

    var awattar = SpotPriceSources.ParseAwattar(
        """{"object":"list","data":[{"start_timestamp":1791050400000,"end_timestamp":1791054000000,"marketprice":223.22,"unit":"Eur/MWh"}]}""");
    Check(awattar.Single().StartUtc == DateTimeOffset.FromUnixTimeMilliseconds(1791050400000).UtcDateTime && Math.Abs(awattar[0].PerKwh - 0.22322) < 1e-9, "aWATTar: time or price is wrong.");

    var nl = SpotPriceSources.ParseEnergyZero("""{"Prices":[{"readingDate":"2026-10-03T00:00:00Z","price":0.173512504}]}""");
    Check(nl.Single() == new SpotSample(utc("2026-10-03T00:00:00"), 0.173512504), "EnergyZero: time or price is wrong.");

    var pl = SpotPriceSources.ParsePse("""{"value":[{"rce_pln":695.73,"dtime_utc":"2026-10-02 22:15:00"}]}""");
    Check(pl.Single().StartUtc == utc("2026-10-02T22:00:00") && Math.Abs(pl[0].PerKwh - 0.69573) < 1e-9, "PSE: dtime_utc is the end of the quarter-hour.");

    var worker = SpotPriceSources.ParseWorker(
        """{"zone":"CZ","currency":"CZK","source":"ENTSO-E Transparency Platform","prices":[{"start":"2026-10-02T22:15:00.000Z","minutes":15,"price":3.415525}]}""");
    Check(worker.Single() == new SpotSample(utc("2026-10-02T22:15:00"), 3.415525), "Worker: time or price is wrong.");
    return Task.CompletedTask;
}

static Task OmieQuarterHours()
{
    var day = string.Join("\n", Enumerable.Range(1, 96).Select(p => $"2026;10;03;{p};{p}.5;{p + 100}.5;"));
    var text = "MARGINALPDBC;\n" + day + "\n*\n";
    var es = SpotPriceSources.ParseOmie(text, 5);
    var pt = SpotPriceSources.ParseOmie(text, 4);
    Check(es.Count == 96 && pt.Count == 96, "OMIE: all 96 quarter-hours must be read.");
    // Midnight in Madrid (CEST, UTC+2) is 22:00 UTC the day before
    Check(es[0].StartUtc == new DateTime(2026, 10, 2, 22, 0, 0, DateTimeKind.Utc), "OMIE: periods must count from Spanish midnight.");
    Check(es[1].StartUtc - es[0].StartUtc == TimeSpan.FromMinutes(15), "OMIE: 96 periods are quarter-hours.");
    Check(Math.Abs(es[0].PerKwh - 0.1015) < 1e-9 && Math.Abs(pt[0].PerKwh - 0.0015) < 1e-9, "OMIE: Spain and Portugal columns are mixed up.");

    var hourly = SpotPriceSources.ParseOmie(string.Join("\n", Enumerable.Range(1, 24).Select(p => $"2026;01;15;{p};50;60;")), 5);
    Check(hourly[1].StartUtc - hourly[0].StartUtc == TimeSpan.FromHours(1), "OMIE: 24 periods are hours.");
    return Task.CompletedTask;
}

static Task CountryCatalog()
{
    Check(Countries.All.Select(c => c.Code).Distinct().Count() == Countries.All.Count, "Country codes must be unique.");
    foreach (var c in Countries.All)
    {
        Check(c.Areas.Count > 0 && c.Unit.PerMajor > 0 && c.VatPercent > 0 && c.VatPercent < 30, $"{c.Code}: areas, unit or VAT is missing.");
        Check(c.AreaOrDefault("nonsense") == c.DefaultArea, $"{c.Code}: an unknown area must fall back to the first.");
    }
    Check(Countries.Find("dk")?.Code == "DK", "Country lookup must ignore case.");
    Check(Countries.Normalize("XX") == Countries.Other && Countries.Find(Countries.Other) == null, "Unknown countries must become Other.");

    var other = new AppSettings { Country = Countries.Other, ShowPrice = true };
    Check(!other.PriceEnabled, "Other country must hide the electricity price.");
    var finland = new AppSettings { Country = "FI" };
    Check(finland.PriceUnit.Symbol == "ct" && finland.EffectiveVatPercent == 25.5, "Finland must use cent and its own VAT.");
    finland.VatPercent = 10;
    Check(finland.EffectiveVatPercent == 10, "A VAT chosen by the user must win.");
    Check(Countries.Find("SE")!.Unit.Format(80.6, System.Globalization.CultureInfo.InvariantCulture) == "81"
          && Countries.Find("FI")!.Unit.Format(7.06, System.Globalization.CultureInfo.InvariantCulture) == "7.1",
          "Units must round to their decimals.");
    return Task.CompletedTask;
}

static async Task CountryPrices()
{
    var requests = new List<string>();
    var now = DateTime.UtcNow;
    var start = DateTime.Today.ToUniversalTime();
    using var http = new HttpClient(new Handler(request =>
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
        requests.Add(url);
        return Task.FromResult(Json(new
        {
            success = true,
            data = new { fi = new[] { new { timestamp = new DateTimeOffset(start).ToUnixTimeSeconds(), price = 50.0 } } }
        }));
    }));
    var service = new ElectricityPriceService(http);

    var settings = new AppSettings { Country = "FI", PriceArea = "DK1", PriceInclVat = true, PriceShowTotal = true, SupplierAddOnOre = 1 };
    await service.RefreshAsync(settings);
    Check(service.LastError == null && service.Prices.Count == 1, "Finland: prices were not fetched from Elering.");
    Check(Math.Abs(service.Prices[0].Spot - 5) < 1e-9, "Finland: 50 EUR/MWh must be 5 ct/kWh.");
    Check(requests.All(r => r.Contains("elering")), "Finland must not fetch Danish tariffs.");
    Check(Math.Abs(service.Consumer(service.Prices[0].Time, service.Prices[0].Spot, settings) - 6 * 1.255) < 1e-9,
          "Finland: spot + add-on with 25.5 % VAT is wrong.");

    requests.Clear();
    settings.Country = Countries.Other;
    Check(service.NeedsRefresh(settings), "Changing the country must trigger a refresh.");
    await service.RefreshAsync(settings);
    Check(service.Prices.Count == 0 && requests.Count == 0, "Other country must clear prices without any request.");
}

static async Task WorkerPrices()
{
    var requests = new List<string>();
    var start = DateTime.Today.ToUniversalTime();
    using var http = new HttpClient(new Handler(request =>
    {
        requests.Add(request.RequestUri!.ToString());
        return Task.FromResult(Json(new
        {
            zone = "CZ",
            currency = "CZK",
            prices = new[] { new { start = start.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), minutes = 15, price = 3.5 } }
        }));
    }));
    var service = new ElectricityPriceService(http);

    var settings = new AppSettings { Country = "CZ", PriceArea = "DK1", PriceInclVat = true, PriceShowTotal = true };
    await service.RefreshAsync(settings);
    Check(service.LastError == null && service.Prices.Count == 1, "Czechia: prices were not fetched from the Worker.");
    var url = requests.Single();
    Check(url.StartsWith(SpotPriceSources.PriceWorker + "/v1/prices?zone=CZ&", StringComparison.Ordinal) && url.EndsWith("&currency=CZK", StringComparison.Ordinal),
          $"Czechia: the Worker must be asked for zone CZ in CZK, not {url}.");
    Check(url.Contains($"from={start:yyyy-MM-dd'T'HH}:00Z", StringComparison.Ordinal), "The Worker window must start at local midnight in whole UTC hours.");
    Check(Math.Abs(service.Prices[0].Spot - 3.5) < 1e-9 && settings.PriceUnit.Symbol == "Kč", "Czechia: prices are shown in Kč/kWh.");
    Check(Math.Abs(service.Consumer(service.Prices[0].Time, service.Prices[0].Spot, settings) - 3.5 * 1.21) < 1e-9, "Czechia: 21 % VAT is wrong.");

    requests.Clear();
    settings.Country = "IT";
    settings.PriceArea = "IT-SICI";
    await service.RefreshAsync(settings);
    Check(requests.Single().Contains("zone=IT-SICI&", StringComparison.Ordinal) && requests[0].EndsWith("&currency=EUR", StringComparison.Ordinal),
          "Italy: the chosen zone must be asked for in EUR.");
}

static Task InstallSuccess() => InSandbox(root =>
{
    var app = Path.Combine(root, "app");
    var staging = app + ".staging";
    Package(app, "old"); Package(staging, "new");
    Directory.CreateDirectory(Path.Combine(app, "webview"));
    File.WriteAllText(Path.Combine(app, "webview", "login"), "session");
    File.WriteAllText(Path.Combine(app, "obsolete.dll"), "old");
    InstallTransaction.Commit(app, staging);
    Check(File.ReadAllText(Path.Combine(app, "LabWidge.exe")) == "new", "New version missing.");
    Check(File.ReadAllText(Path.Combine(app, "webview", "login")) == "session", "Login was lost.");
    Check(!File.Exists(Path.Combine(app, "obsolete.dll")) && !Directory.Exists(app + ".backup"), "Old program files remain.");
});

static Task InstallRollback() => InSandbox(root =>
{
    var app = Path.Combine(root, "app");
    var staging = app + ".staging";
    Package(app, "old"); Package(staging, "new");
    try
    {
        InstallTransaction.Commit(app, staging, (source, target) =>
        {
            if (source == staging) throw new IOException("Simulated failure after old directory moved.");
            Directory.Move(source, target);
        });
        throw new Exception("Expected switch failure.");
    }
    catch (IOException) { }
    Check(File.ReadAllText(Path.Combine(app, "LabWidge.exe")) == "old", "Rollback lost the old executable.");
    Check(File.ReadAllText(Path.Combine(app, "library.dll")) == "old", "Rollback lost dependencies.");
});

static Task InstallRecovery() => InSandbox(root =>
{
    var app = Path.Combine(root, "app");
    Package(app + ".backup", "old");
    InstallTransaction.Recover(app);
    Check(File.ReadAllText(Path.Combine(app, "LabWidge.exe")) == "old", "Interrupted first rename was not recovered.");
    Directory.Move(app, app + ".backup");
    Directory.CreateDirectory(Path.Combine(app + ".backup", "webview"));
    File.WriteAllText(Path.Combine(app + ".backup", "webview", "login"), "session");
    Package(app, "new");
    InstallTransaction.Recover(app);
    Check(File.ReadAllText(Path.Combine(app, "LabWidge.exe")) == "new", "Committed new version was rolled back.");
    Check(File.ReadAllText(Path.Combine(app, "webview", "login")) == "session", "Recovery lost legacy login.");
});

static Task InstallRetry() => InSandbox(root =>
{
    var app = Path.Combine(root, "app");
    var staging = app + ".staging";
    Package(app, "old"); Package(staging, "new");
    try
    {
        InstallTransaction.Commit(app, staging, (source, target) =>
        {
            Directory.Move(source, target);
            if (source == staging) throw new IOException("Simulated failure after new directory moved.");
        });
        throw new Exception("Expected switch failure.");
    }
    catch (IOException) { }
    Check(File.ReadAllText(Path.Combine(app, "LabWidge.exe")) == "old", "Old version was not restored.");
    Check(File.ReadAllText(Path.Combine(staging, "LabWidge.exe")) == "new", "Prepared package was lost during rollback.");
    InstallTransaction.Commit(app, staging);
    Check(File.ReadAllText(Path.Combine(app, "LabWidge.exe")) == "new", "Retry could not install the preserved package.");
});

static Task InvalidPackage() => InSandbox(root =>
{
    var app = Path.Combine(root, "app");
    Package(app, "old");
    Directory.CreateDirectory(app + ".staging");
    try { InstallTransaction.Commit(app, app + ".staging"); throw new Exception("Expected invalid package failure."); }
    catch (InvalidOperationException) { }
    Check(File.ReadAllText(Path.Combine(app, "LabWidge.exe")) == "old", "Invalid package damaged installation.");
});

static async Task UninstallWait()
{
    var root = Path.Combine(Path.GetTempPath(), "labwidge-test-" + Guid.NewGuid().ToString("N"));
    var target = Path.Combine(root, "widget's files");
    Directory.CreateDirectory(target);
    File.WriteAllText(Path.Combine(target, "locked.exe"), "test");
    try
    {
        try { UninstallCleanup.StartInfo(root, Environment.ProcessId); throw new Exception("Unsafe cleanup path accepted."); }
        catch (InvalidOperationException) { }
        using var parent = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 5")
            { UseShellExecute = false, CreateNoWindow = true })!;
        var script = UninstallCleanup.BuildScript(target, parent.Id);
        using var cleanup = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            { UseShellExecute = false, CreateNoWindow = true })!;
        await Task.Delay(2500);
        Check(Directory.Exists(target) && !cleanup.HasExited, "Cleanup ran before parent process exited.");
        await cleanup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Check(cleanup.ExitCode == 0 && !Directory.Exists(target), "Cleanup did not delete the sandbox after process exit.");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

static Task InSandbox(Action<string> run)
{
    var root = Path.Combine(Path.GetTempPath(), "labwidge-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { run(root); }
    finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
}

static void Package(string dir, string version)
{
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "LabWidge.exe"), version);
    File.WriteAllText(Path.Combine(dir, "library.dll"), version);
}

static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
};

// ---------- Quarter-hour prices, appliances, rules and history ----------

/// <summary>Quarter-hours from <paramref name="start"/> with a price per quarter.</summary>
static List<PricePeriod> Quarters(DateTime start, params double[] values) =>
    PriceWindows.FromPoints(values.Select((v, i) => (start.AddMinutes(15 * i), v)).ToList());

/// <summary>A price service holding <paramref name="prices"/> as if they were fetched – the consumer price equals the spot price.</summary>
static (ElectricityPriceService El, AppSettings S) PriceServiceWith(IEnumerable<PricePoint> prices)
{
    var el = new ElectricityPriceService(new HttpClient());
    typeof(ElectricityPriceService).GetProperty(nameof(ElectricityPriceService.Prices))!.SetValue(el, prices.ToList());
    var s = new AppSettings { Country = "DE", PriceInclVat = false, PriceShowTotal = false, OnboardingCompleted = true };
    return (el, s);
}

static Task QuarterWindows()
{
    var day = new DateTime(2026, 10, 17);
    // 12:00–15:00 in quarters: expensive, then cheap from 12:45 for 3 quarters, then medium
    var p = Quarters(day.AddHours(12), 50, 50, 50, 10, 10, 10, 30, 30, 30, 30, 30, 30);
    Check(p.Count == 12 && p[0].Length == TimeSpan.FromMinutes(15) && p[^1].End == day.AddHours(15), "Quarters must last 15 minutes.");

    var best = PriceWindows.Cheapest(p, day.AddHours(12), TimeSpan.FromMinutes(45));
    Check(best is { } b && b.Start == day.AddHours(12).AddMinutes(45) && Math.Abs(b.Average - 10) < 1e-9, "The cheapest 45 minutes start at 12:45.");

    // Not aligned to the hour, and a length that is not a whole number of periods
    var odd = PriceWindows.Cheapest(p, day.AddHours(12), TimeSpan.FromMinutes(50));
    Check(odd is { } o && o.Start == day.AddHours(12).AddMinutes(45) && Math.Abs(o.Average - (10 * 45 + 30 * 5) / 50.0) < 1e-9,
        "A stretch of 50 minutes is averaged by time.");

    // From the middle of the current period, the current period still counts – unless the start must be now or later
    Check(PriceWindows.Cheapest(p, day.AddHours(12).AddMinutes(50), TimeSpan.FromMinutes(30))!.Value.Start == day.AddHours(12).AddMinutes(45),
        "The period already begun can be the cheapest.");
    var now = day.AddHours(12).AddMinutes(50);
    var fromNow = PriceWindows.Cheapest(p, now, TimeSpan.FromMinutes(30), startNow: true)!.Value;
    Check(fromNow.Start == now && Math.Abs(fromNow.Average - 10) < 1e-9, "Starting now uses the rest of the current period.");

    Check(PriceWindows.Cheapest(p, day.AddHours(12), TimeSpan.FromHours(4)) == null, "A stretch longer than the prices has no answer.");
    Check(PriceWindows.Cheapest(p, day.AddHours(12), TimeSpan.FromMinutes(45), latestEnd: day.AddHours(13))!.Value.Start == day.AddHours(12).AddMinutes(15),
        "The latest end is respected.");
    Check(PriceWindows.Cheapest(p, day.AddHours(12), TimeSpan.FromMinutes(15), cheapest: false)!.Value.Average == 50, "The most expensive stretch is found too.");

    var set = PriceWindows.CheapestSet(p, day.AddHours(12), day.AddHours(15), TimeSpan.FromMinutes(60));
    Check(set.Count == 4 && set.Take(3).All(x => x.Value == 10) && set[3].Value == 30, "The cheapest hour is the four cheapest quarters.");
    var merged = PriceWindows.Merge(set);
    Check(merged.Count == 1 && merged[0].Start == day.AddHours(12).AddMinutes(45) && merged[0].End == day.AddHours(13).AddMinutes(45),
        "Quarters side by side are merged.");

    // Part of an hour: 1¼ hours of hourly prices ends a quarter into the second hour
    var hours = PriceWindows.FromPoints(new List<(DateTime, double)> { (day, 3), (day.AddHours(1), 1), (day.AddHours(2), 2) });
    var part = PriceWindows.CheapestSet(hours, day, day.AddHours(3), TimeSpan.FromMinutes(75));
    Check(part.Count == 2 && part[0].Start == day.AddHours(1) && part[0].Length == TimeSpan.FromHours(1)
          && part[1].Start == day.AddHours(2) && part[1].End == day.AddHours(2).AddMinutes(15), "A part of an hour must cut the last hour short.");
    // A stretch that is not a whole number of periods can be cheapest when it ends on a boundary
    var aligned = PriceWindows.FromPoints(new List<(DateTime, double)> { (day, 10), (day.AddHours(1), 0), (day.AddHours(2), 100) });
    var ninety = PriceWindows.Cheapest(aligned, day, TimeSpan.FromMinutes(90))!.Value;
    Check(ninety.Start == day.AddMinutes(30) && ninety.End == day.AddHours(2) && Math.Abs(ninety.Average - 10 * 30 / 90.0) < 1e-9,
        "90 minutes on hourly prices must be able to start at 00:30 and end at 02:00.");
    var rule = new PriceAutomation { Hours = 1.25, FromHour = 0, ToHour = 3 };
    Check(AutomationRules.Applies(rule, hours, new List<double> { 3, 1, 2 }, day.AddHours(2).AddMinutes(10)) == true
          && AutomationRules.Applies(rule, hours, new List<double> { 3, 1, 2 }, day.AddHours(2).AddMinutes(20)) == false,
        "A rule of 1¼ hours must stop a quarter into the hour.");

    // Hourly data, and a gap in the data is not stretched over
    var hourly = PriceWindows.FromPoints(new List<(DateTime, double)> { (day, 1), (day.AddHours(1), 2), (day.AddHours(5), 3) });
    Check(hourly[1].End == day.AddHours(2) && hourly[2].Length == TimeSpan.FromHours(1), "A gap must not make a period longer than an hour.");
    Check(PriceWindows.Average(hourly, day.AddHours(1), day.AddHours(3)) == null, "An average across a gap has no answer.");

    // Around a clock change (in this PC's time zone, if it has one) the periods follow each other without gaps or negative lengths
    foreach (var utcDay in new[] { new DateTime(2027, 3, 27, 12, 0, 0, DateTimeKind.Utc), new DateTime(2027, 10, 30, 12, 0, 0, DateTimeKind.Utc) })
    {
        var local = PriceWindows.FromPoints(Enumerable.Range(0, 96).Select(i => (utcDay.AddMinutes(15 * i).ToLocalTime(), 1.0)).ToList());
        Check(local.All(x => x.End > x.Start && x.Length <= TimeSpan.FromHours(1)), "A clock change must not give a period a negative or long length: " + string.Join("; ", local.Where(x => !(x.End > x.Start && x.Length <= TimeSpan.FromHours(1))).Select(x => $"{x.Start:O}-{x.End:O} {x.Length}")));
        Check(local.Zip(local.Skip(1)).All(x => x.First.End == x.Second.Start || x.Second.Start < x.First.Start.AddMinutes(1) || x.First.End > x.Second.Start),
            "A clock change must not leave a gap between periods.");
    }
    return Task.CompletedTask;
}

static Task QuarterPriceAlerts()
{
    var day = DateTime.Today;
    // 40 all day, cheap 13:15–16:15 and expensive 17–20
    var prices = Enumerable.Range(0, 96).Select(i =>
    {
        var t = day.AddMinutes(15 * i);
        var cheap = t >= day.AddHours(13).AddMinutes(15) && t < day.AddHours(16).AddMinutes(15);
        var expensive = t >= day.AddHours(17) && t < day.AddHours(20);
        return new PricePoint(t, cheap ? 5 : expensive ? 80 : 40);
    });
    var (el, s) = PriceServiceWith(prices);
    s.AlertQuietNight = false;
    s.AlertExpensive = false;
    s.AlertLeadMinutes = 30;

    var alerts = PriceAlerts.Evaluate(el, s, day.AddHours(12).AddMinutes(50)).ToList();
    Check(alerts.Count == 1 && alerts[0].StartsAt == day.AddHours(13).AddMinutes(15), "The cheap alert must start at the quarter-hour 13:15.");
    Check(alerts[0].Line1.Contains("13:15") && alerts[0].Line1.Contains("16:15"), "The alert must name 13:15–16:15.");
    s.LastCheapAlertFor = alerts[0].StartsAt;
    Check(!PriceAlerts.Evaluate(el, s, day.AddHours(13)).Any(), "The same cheap stretch is only announced once.");
    Check(!PriceAlerts.Evaluate(el, s, day.AddHours(12)).Any(), "No alert before the lead time.");

    // The expensive alert names the quarter the expensive stretch starts at
    s.AlertCheap = false;
    s.AlertExpensive = true;
    var expensive = PriceAlerts.Evaluate(el, s, day.AddHours(16).AddMinutes(40)).ToList();
    Check(expensive.Count == 1 && expensive[0].StartsAt == day.AddHours(17) && expensive[0].Line2.Contains("20:00"), "The expensive alert must start at 17:00.");
    return Task.CompletedTask;
}

static Task AppliancePlans()
{
    var day = new DateTime(2026, 10, 17);
    // Hourly prices from 18:00 for 14 hours: cheapest 02:00–04:00
    var values = new double[] { 40, 38, 36, 30, 25, 20, 15, 12, 5, 5, 12, 20, 30, 40 };
    var p = PriceWindows.FromPoints(values.Select((v, i) => (day.AddHours(18 + i), v)).ToList());
    var dishwasher = new Appliance { Name = "Dishwasher", DurationMinutes = 120, EnergyKwh = 1.0, FinishBy = 7 * 60 };
    var now = day.AddHours(18).AddMinutes(10);

    Check(AppliancePlanner.Deadline(dishwasher, now) == day.AddDays(1).AddHours(7), "Finished by 07:00 means tomorrow morning.");
    Check(AppliancePlanner.Deadline(dishwasher, day.AddHours(5)) == day.AddHours(7), "At 05:00 there is still room before 07:00 today.");
    Check(AppliancePlanner.Deadline(dishwasher, day.AddHours(5).AddMinutes(30)) == day.AddDays(1).AddHours(7), "Without room for a run the deadline is tomorrow.");

    var plan = AppliancePlanner.Plan(dishwasher, p, now)!;
    Check(plan.Best.Start == day.AddDays(1).AddHours(2) && plan.Best.End == day.AddDays(1).AddHours(4), "The dishwasher must start at 02:00.");
    var unit = Countries.Find("DK")!.Unit;
    Check(Math.Abs(plan.Cost(dishwasher, unit)!.Value - 0.05) < 1e-9, "1 kWh at 5 øre costs 0.05 kr.");
    Check(plan.Saving(dishwasher, unit) > 0.3, "Starting at 02:00 must save compared with now.");

    // A deadline before the cheapest hours moves the start earlier
    var early = new Appliance { DurationMinutes = 60, FinishBy = 0 };
    Check(AppliancePlanner.Plan(early, p, now)!.Best.End <= day.AddDays(1), "The deadline must be kept.");

    // Plan, then cheaper prices arrive: the run moves
    var s = new AppSettings { Appliances = { dishwasher } };
    Check(AppliancePlanner.Toggle(s, dishwasher, p, now) && s.PlannedRuns.Count == 1, "Planning must add a run.");
    var run = s.PlannedRuns[0];
    var cheaper = PriceWindows.FromPoints(values.Select((v, i) => (day.AddHours(18 + i), i == 3 || i == 4 ? 1.0 : v)).ToList());
    Check(AppliancePlanner.Replan(run, dishwasher, cheaper, now) && run.Start == day.AddHours(21), "New, cheaper prices must move the planned run.");
    Check(!AppliancePlanner.Replan(run, dishwasher, cheaper, now), "An unchanged plan does not move.");
    run.Started = true;
    Check(!AppliancePlanner.Replan(run, dishwasher, p, now), "A started run never moves.");
    Check(AppliancePlanner.Toggle(s, dishwasher, p, now) && s.PlannedRuns.Count == 0, "Planning again cancels the run.");

    var clone = s.Clone();
    Check(clone.Appliances.Count == 1 && clone.Appliances[0].FinishBy == 7 * 60 && clone.Appliances[0].Id == dishwasher.Id, "Appliances must survive cloning.");
    return Task.CompletedTask;
}

static Task AutomationRuleChecks()
{
    var day = new DateTime(2026, 10, 17);
    var overnight = new PriceAutomation { Condition = AutomationCondition.CheapestHours, Hours = 2, FromHour = 18, ToHour = 7, EntityId = "switch.car" };
    Check(AutomationRules.Range(overnight, day.AddHours(20)) == (day.AddHours(18), day.AddDays(1).AddHours(7)), "18–07 at 20:00 is tonight.");
    Check(AutomationRules.Range(overnight, day.AddHours(3)) == (day.AddDays(-1).AddHours(18), day.AddHours(7)), "18–07 at 03:00 began yesterday.");
    Check(AutomationRules.Range(overnight, day.AddHours(12)) == null, "18–07 does not apply at noon.");
    var whole = new PriceAutomation { FromHour = 0, ToHour = 24 };
    Check(AutomationRules.Range(whole, day.AddHours(12)) == (day, day.AddDays(1)), "00–24 is the whole day.");

    var values = new double[] { 40, 38, 36, 30, 25, 20, 15, 12, 5, 6, 12, 20, 30, 40 };
    var p = PriceWindows.FromPoints(values.Select((v, i) => (day.AddHours(18 + i), v)).ToList());
    var hourly = values.ToList();
    Check(AutomationRules.Applies(overnight, p, hourly, day.AddDays(1).AddHours(2).AddMinutes(30)) == true, "02:00–04:00 are the cheapest 2 hours.");
    Check(AutomationRules.Applies(overnight, p, hourly, day.AddHours(22)) == false, "22:00 is not among the cheapest hours.");
    var evening = p.Where(x => x.Start < day.AddDays(1)).ToList(); // tomorrow's prices are missing
    Check(AutomationRules.Applies(overnight, evening, hourly, day.AddHours(23).AddMinutes(30)) == false,
        "A rule must not pick the cheapest hours before the whole range's prices are known.");
    Check(AutomationRules.Applies(overnight, p, hourly, day.AddDays(2)) == null, "Without a price the rule is undecided.");
    var upcoming = AutomationRules.Upcoming(overnight, p, hourly, day.AddHours(19));
    Check(upcoming.Count == 1 && upcoming[0].Start == day.AddDays(1).AddHours(2) && upcoming[0].End == day.AddDays(1).AddHours(4), "The coming stretch is 02–04.");

    var below = new PriceAutomation { Condition = AutomationCondition.PriceBelow, Threshold = 13 };
    Check(AutomationRules.Upcoming(below, p, hourly, day.AddHours(18)).Single().Start == day.AddDays(1).AddHours(1), "Below 13 starts at 01:00.");

    // Transitions: act only on change, revert only when asked
    Check(AutomationRules.Action(overnight, true, null) == true, "A new rule that applies switches on.");
    Check(AutomationRules.Action(overnight, false, null) == null, "A new rule that doesn't apply yet leaves the entity alone.");
    Check(AutomationRules.Action(overnight, true, true) == null, "Nothing is sent while the rule keeps applying.");
    Check(AutomationRules.Action(overnight, false, true) == false, "The rule switches off afterwards.");
    var noRevert = new PriceAutomation { TurnOn = false, Revert = false };
    Check(AutomationRules.Action(noRevert, true, false) == false && AutomationRules.Action(noRevert, false, true) == null, "A rule without revert leaves the entity alone afterwards.");

    var changed = new PriceAutomation { Id = overnight.Id, Hours = 3, FromHour = 18, ToHour = 7, EntityId = "switch.car" };
    Check(changed.Signature != overnight.Signature, "An edited rule must be applied again.");
    var culture = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        var half = new PriceAutomation { Id = "x", Hours = 1.5, Threshold = 2.5 };
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("da-DK");
        var danish = half.Signature;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
        Check(danish == half.Signature, "A rule's signature must not depend on the PC's number format.");
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    var s = new AppSettings { PriceAutomations = { overnight } }.Clone();
    Check(s.PriceAutomations.Single().Condition == AutomationCondition.CheapestHours && s.PriceAutomations[0].ToHour == 7, "Rules must survive cloning.");
    return Task.CompletedTask;
}

static Task PriceHistoryDays()
{
    var folder = Path.Combine(Path.GetTempPath(), "labwidge-history-" + Guid.NewGuid().ToString("N"));
    PriceHistory.FilePath = Path.Combine(folder, "price-history.json");
    PriceHistory.Reset();
    try
    {
        var today = new DateTime(2026, 10, 17);
        var yesterday = today.AddDays(-1);
        var prices = Enumerable.Range(0, 96).Select(i => new PricePoint(yesterday.AddMinutes(15 * i), i % 4))
            .Concat(Enumerable.Range(0, 40).Select(i => new PricePoint(today.AddMinutes(15 * i), 9))).ToList();
        Check(PriceHistory.Record(prices, (_, spot) => spot * 2, "DK:DK1", 365, today), "A whole day must be kept.");
        var days = PriceHistory.DaysFor("DK:DK1");
        Check(days.Count == 1 && days[0].Date == yesterday && days[0].Minutes == 15 && days[0].Price.Length == 96, "Only the whole day is kept, in quarters.");
        Check(Math.Abs(days[0].Average - 3) < 1e-9 && days[0].Spot[1] == 1, "The price and the spot price are both kept.");
        Check(!PriceHistory.Record(prices, (_, spot) => spot * 2, "DK:DK1", 365, today), "The same day is not saved twice.");
        Check(PriceHistory.DaysFor("DK:DK2").Count == 0, "Price areas are kept apart.");
        Check(!PriceHistory.Record(prices.Where((_, i) => i != 50).ToList(), (_, spot) => spot, "DK:DK3", 365, today), "A day with a missing period is not whole.");
        Check(!PriceHistory.Record(prices, (_, spot) => spot * 3, "DK:DK1", 365, today) && Math.Abs(PriceHistory.DaysFor("DK:DK1")[0].Average - 3) < 1e-9,
            "Periods that have passed keep the price they had.");
        Check(PriceHistory.MonthAverage("DK:DK1", today) is { } m && Math.Abs(m - 3) < 1e-9, "The month's average comes from the kept days.");

        PriceHistory.AddSaving(new SavingRecord { Start = today, Name = "Dishwasher", Saved = 1.5, Currency = "DKK" });
        Check(PriceHistory.MonthSavings("DKK", today) == (1.5, 1), "Savings are summed per month and currency.");

        PriceHistory.Reset();
        Check(PriceHistory.DaysFor("DK:DK1").Count == 1 && PriceHistory.Data.Savings.Count == 1, "The history is read back from the file.");
        var csv = PriceHistory.ToCsv("DK:DK1").Split('\n');
        Check(csv[0] == "start,spot,price" && csv[1] == "2026-10-16 00:00,0,0" && csv[2] == "2026-10-16 00:15,1,2", "The CSV has one line per period.");
        var kept = PriceHistory.DaysFor("DK:DK1")[0];
        Check(kept.StartUtc == yesterday.ToUniversalTime() && kept.PeriodStart(95) == DateTime.SpecifyKind(yesterday.ToUniversalTime(), DateTimeKind.Utc).AddMinutes(95 * 15).ToLocalTime(),
            "Periods are placed in UTC, so a clock change keeps the right times.");

        // Old days and savings are forgotten
        PriceHistory.Record(new[] { new PricePoint(today, 1) }, (_, s) => s, "DK:DK1", 7, today.AddDays(30));
        Check(PriceHistory.DaysFor("DK:DK1").Count == 0 && PriceHistory.Data.Savings.Count == 0, "Days older than the limit are removed.");
    }
    finally
    {
        PriceHistory.Reset();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }
    return Task.CompletedTask;
}


// ---------- Price actions: planned runs and rules against a fake Home Assistant ----------

/// <summary>A runner with hourly prices from <paramref name="day"/> (cheap 02–04), a fake Home Assistant and a clock the test moves.</summary>
static (PriceActionRunner Runner, AppSettings S, FakeHomeAssistant Ha, List<string> Toasts, Action<DateTime> SetNow) RunnerFixture(DateTime day)
{
    var values = new double[] { 40, 40, 5, 5, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40,
                                40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40 };
    var (el, s) = PriceServiceWith(values.Select((v, i) => new PricePoint(day.AddHours(i), v)));
    s.HomeAssistantEnabled = true;
    s.HomeAssistantUrl = "http://ha.test";
    s.SetPluginEnabled("ha", true);
    s.PriceHistoryEnabled = false;
    var ha = new FakeHomeAssistant();
    var toasts = new List<string>();
    var now = day;
    var runner = new PriceActionRunner(el, ha.SetAsync, () => s, () => { }, (_, title, _, _) => toasts.Add(title), () => now);
    return (runner, s, ha, toasts, t => now = t);
}

static async Task PlannedRunFlow()
{
    var day = new DateTime(2026, 11, 10);
    var (runner, s, ha, toasts, setNow) = RunnerFixture(day);
    var plug = new Appliance { Name = "Opvask", DurationMinutes = 120, EnergyKwh = 1, HomeAssistantEntity = "switch.opvask", TurnOffAfter = true };
    s.Appliances.Add(plug);
    var el = (ElectricityPriceService)typeof(PriceActionRunner).GetField("_prices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(runner)!;
    Check(AppliancePlanner.Toggle(s, plug, el.ConsumerPeriods(s), day) && s.PlannedRuns[0].Start == day.AddHours(2), "The run must be planned for 02:00.");

    setNow(day.AddHours(1));
    await runner.TickAsync();
    Check(ha.Calls.Count == 0, "Nothing is switched before the planned start.");

    setNow(day.AddHours(2));
    await runner.TickAsync();
    Check(ha.Calls.SequenceEqual(new[] { ("switch.opvask", true, (string?)"http://ha.test") }), "The plug must be switched on at the start.");

    // Edited and moved to another entity during the run: the original one is switched off
    plug.HomeAssistantEntity = "switch.other";
    plug.TurnOffAfter = false;
    setNow(day.AddHours(3));
    await runner.TickAsync();
    Check(ha.Calls.Count == 1, "Nothing is switched during the run.");

    // Home Assistant is down at the end: the run is kept and tried again
    ha.Down = true;
    setNow(day.AddHours(4));
    await runner.TickAsync();
    Check(s.PlannedRuns.Count == 1, "A failed switch-off must keep the run so it is tried again.");
    ha.Down = false;
    setNow(day.AddHours(4).AddMinutes(1));
    await runner.TickAsync();
    Check(ha.Calls.Last() == ("switch.opvask", false, "http://ha.test") && s.PlannedRuns.Count == 0,
        "The original plug must be switched off once Home Assistant answers, and the run finished.");
    Check(ha.Calls.Count(c => !c.On) == 1, "The plug is switched off once.");
    await runner.TickAsync();
    Check(ha.Calls.Count == 2, "A finished run does nothing more.");
}

static async Task PlannedRunSafety()
{
    var day = new DateTime(2026, 11, 10);

    // Start fails: the user is asked to start it, and it is never switched off by LabWidge
    {
        var (runner, s, ha, toasts, setNow) = RunnerFixture(day);
        var plug = new Appliance { Name = "Opvask", DurationMinutes = 60, HomeAssistantEntity = "switch.opvask", TurnOffAfter = true };
        s.Appliances.Add(plug);
        s.PlannedRuns.Add(new PlannedRun { ApplianceId = plug.Id, Start = day.AddHours(2), End = day.AddHours(3), Average = 5 });
        ha.Down = true;
        setNow(day.AddHours(2));
        await runner.TickAsync();
        ha.Down = false;
        setNow(day.AddHours(3));
        await runner.TickAsync();
        Check(ha.Calls.Count == 0 && s.PlannedRuns.Count == 0 && toasts.Any(t => t.Contains("Opvask")),
            "A plug LabWidge couldn't switch on must not be switched off by it later.");
    }

    // The address changes during the run: nothing is switched off on the new server, the user is told
    {
        var (runner, s, ha, toasts, setNow) = RunnerFixture(day);
        var plug = new Appliance { Name = "Opvask", DurationMinutes = 60, HomeAssistantEntity = "switch.opvask", TurnOffAfter = true };
        s.Appliances.Add(plug);
        s.PlannedRuns.Add(new PlannedRun { ApplianceId = plug.Id, Start = day.AddHours(2), End = day.AddHours(3), Average = 5 });
        setNow(day.AddHours(2));
        await runner.TickAsync();
        s.HomeAssistantUrl = "http://other.test";
        setNow(day.AddHours(3));
        await runner.TickAsync();
        Check(ha.Calls.Count == 1 && toasts.Count == 2 && s.PlannedRuns.Count == 0, "A changed address must give a warning, not a switch-off elsewhere.");
    }

    // The price plugin is switched off during the run: it is still switched off at the end
    {
        var (runner, s, ha, _, setNow) = RunnerFixture(day);
        var plug = new Appliance { Name = "Opvask", DurationMinutes = 60, HomeAssistantEntity = "switch.opvask", TurnOffAfter = true };
        s.Appliances.Add(plug);
        s.PlannedRuns.Add(new PlannedRun { ApplianceId = plug.Id, Start = day.AddHours(2), End = day.AddHours(3), Average = 5 });
        setNow(day.AddHours(2));
        await runner.TickAsync();
        s.SetPluginEnabled("price", false);
        s.SetPluginEnabled("ha", false);
        s.Appliances.Clear();
        setNow(day.AddHours(3));
        await runner.TickAsync();
        Check(ha.Calls.Count == 2 && !ha.Calls[1].On, "A started run must be switched off even with its plugins off and its appliance removed.");
    }

    // Woken late: the whole run time if it fits before the deadline, otherwise not started
    {
        var (runner, s, ha, toasts, setNow) = RunnerFixture(day);
        var plug = new Appliance { Name = "Opvask", DurationMinutes = 120, HomeAssistantEntity = "switch.opvask", TurnOffAfter = true };
        s.Appliances.Add(plug);
        s.PlannedRuns.Add(new PlannedRun { ApplianceId = plug.Id, Start = day.AddHours(2), End = day.AddHours(4), Deadline = day.AddHours(5), Average = 5 });
        setNow(day.AddHours(2).AddMinutes(20));
        await runner.TickAsync();
        Check(s.PlannedRuns[0].End == day.AddHours(4).AddMinutes(20), "A late start must get the whole run time.");
        s.PlannedRuns.Clear();
        ha.Calls.Clear();
        s.PlannedRuns.Add(new PlannedRun { ApplianceId = plug.Id, Start = day.AddHours(2), End = day.AddHours(4), Deadline = day.AddHours(4), Average = 5 });
        setNow(day.AddHours(2).AddMinutes(20));
        await runner.TickAsync();
        Check(ha.Calls.Count == 0 && s.PlannedRuns.Count == 0, "A late start that can't finish by the deadline must not start.");
        // A start a few seconds late is on time
        s.PlannedRuns.Add(new PlannedRun { ApplianceId = plug.Id, Start = day.AddHours(2), End = day.AddHours(4), Deadline = day.AddHours(4), Average = 5 });
        setNow(day.AddHours(2).AddSeconds(20));
        await runner.TickAsync();
        Check(ha.Calls.Count == 1 && s.PlannedRuns[0].Started, "A start seconds late must still run.");
    }
}

static async Task RuleFlow()
{
    var day = new DateTime(2026, 11, 10);
    var (runner, s, ha, toasts, setNow) = RunnerFixture(day);
    var rule = new PriceAutomation { Name = "VVB", EntityId = "switch.vvb", Hours = 2 };
    s.PriceAutomations.Add(rule);

    setNow(day.AddHours(1));
    await runner.TickAsync();
    Check(ha.Calls.Count == 0, "A new rule that doesn't apply yet does nothing.");
    setNow(day.AddHours(2).AddMinutes(5));
    await runner.TickAsync();
    await runner.TickAsync();
    Check(ha.Calls.SequenceEqual(new[] { ("switch.vvb", true, (string?)"http://ha.test") }), "The rule switches on once when its hours start.");
    setNow(day.AddHours(4).AddMinutes(5));
    await runner.TickAsync();
    Check(ha.Calls.Last() == ("switch.vvb", false, "http://ha.test") && ha.Calls.Count == 2, "The rule switches back when its hours end.");

    // Edited while it applies: the old entity is put back before the new one is used
    setNow(day.AddHours(2).AddMinutes(10));
    await runner.TickAsync();
    rule.EntityId = "switch.new";
    ha.Down = true;
    setNow(day.AddHours(2).AddMinutes(20));
    await runner.TickAsync();
    Check(ha.Calls.Count == 3 && s.RuleStates[rule.Id].EntityId == "switch.vvb", "A failed revert keeps the old state, and the edited rule waits.");
    ha.Down = false;
    await runner.TickAsync();
    Check(ha.Calls[3] == ("switch.vvb", false, "http://ha.test") && ha.Calls[4] == ("switch.new", true, "http://ha.test"),
        "The old entity is switched back, then the edited rule switches its new one.");

    // Switched off in the settings: put back
    rule.Enabled = false;
    await runner.TickAsync();
    Check(ha.Calls.Last() == ("switch.new", false, "http://ha.test") && s.RuleStates.Count == 0, "A rule switched off puts its entity back.");

    // The prices run out while it applies: put back
    rule.Enabled = true;
    await runner.TickAsync();
    var count = ha.Calls.Count;
    setNow(day.AddDays(3));
    await runner.TickAsync();
    Check(ha.Calls.Count == count + 1 && !ha.Calls.Last().On, "A rule whose prices ran out puts its entity back.");

    // Two rules for one entity: only the first runs
    s.PriceAutomations.Add(new PriceAutomation { Name = "Twin", EntityId = "SWITCH.NEW", Condition = AutomationCondition.Expensive });
    setNow(day.AddHours(10));
    count = ha.Calls.Count;
    await runner.TickAsync();
    Check(ha.Calls.Count == count, "A second rule for the same entity must not run.");
}

sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}

sealed class CancelHandler : HttpMessageHandler
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Block { get; set; } = true;
    public int Requests { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (Block) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
        var body = request.RequestUri!.ToString().Contains("DayAheadPrices")
            ? JsonSerializer.Serialize(new { records = new[] { new { TimeUTC = DateTime.Today.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss"), DayAheadPriceDKK = 1000 } } })
            : "{\"records\":[]}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}

/// <summary>A fake Home Assistant: records every switch, and fails while <see cref="Down"/> is set.</summary>
sealed class FakeHomeAssistant
{
    public List<(string Entity, bool On, string? Url)> Calls { get; } = new();
    public bool Down { get; set; }
    public Task SetAsync(string entity, bool on, AppSettings s, CancellationToken cancel)
    {
        if (Down) throw new HttpRequestException("Home Assistant is down");
        Calls.Add((entity, on, s.HomeAssistantUrl));
        return Task.CompletedTask;
    }
}
