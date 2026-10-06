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
    ("DNS: empty and whitespace-only host lists make no requests", EmptyHosts),
    ("DNS: selected hosts and all hosts remain distinct", SelectedHosts),
    ("DNS: failed updates are retried and successful updates stop retrying", RetryDns),
    ("DNS: settings changed during a request remain pending", ChangedDnsSettings),
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
    ("Shortcuts: websites, programs and other addresses are told apart and survive cloning", LaunchItems)
};
foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine("PASS " + test.Name);
}
Console.WriteLine($"{tests.Length} regression tests passed.");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
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

static async Task EmptyHosts()
{
    using var http = new HttpClient(new Handler(_ => throw new Exception("An empty selection must not contact DNS.")));
    Check(await CloudflareClient.UpdateAllARecordsAsync("zone", "test", "203.0.113.1", Array.Empty<string>(), http) == 0, "Empty list updated records.");
    Check(await CloudflareClient.UpdateAllARecordsAsync("zone", "test", "203.0.113.1", new[] { " ", "\t" }, http) == 0, "Whitespace updated records.");
}

static async Task SelectedHosts()
{
    var changed = new List<string>();
    using var http = new HttpClient(new Handler(request =>
    {
        if (request.Method == HttpMethod.Get)
            return Task.FromResult(Json(new { success = true, result = new[]
            {
                new { id = "one", type = "A", name = "one.example.test", content = "192.0.2.1", ttl = 1, proxied = false },
                new { id = "two", type = "A", name = "two.example.test", content = "192.0.2.1", ttl = 1, proxied = false }
            } }));
        changed.Add(request.RequestUri!.Segments[^1]);
        return Task.FromResult(Json(new { success = true }));
    }));
    Check(await CloudflareClient.UpdateAllARecordsAsync("zone", "test", "203.0.113.1", new[] { " ONE.example.test ", "one.example.test" }, http) == 1, "Selection did not update exactly one record.");
    Check(changed.SequenceEqual(new[] { "one" }), "An unselected record changed.");
    changed.Clear();
    Check(await CloudflareClient.UpdateAllARecordsAsync("zone", "test", "203.0.113.1", null, http) == 2, "All-host mode did not update both records.");
}

static async Task RetryDns()
{
    var sync = new CloudflareDnsSync();
    const string ip = "203.0.113.1";
    var calls = 0;
    Check(sync.NeedsUpdate(ip), "First lookup must sync.");
    try
    {
        await sync.SyncAsync(ip, () => { calls++; throw new HttpRequestException("offline"); });
        throw new Exception("Expected the DNS request to fail.");
    }
    catch (HttpRequestException) { }
    Check(sync.NeedsUpdate(ip), "A failure suppressed retry for the same IP.");
    await sync.SyncAsync(ip, () => { calls++; return Task.FromResult(0); });
    Check(calls == 2 && !sync.NeedsUpdate(ip), "A successful no-change response must stop retries.");
    Check(sync.NeedsUpdate("203.0.113.2"), "A new IP must sync.");
}

static async Task ChangedDnsSettings()
{
    var sync = new CloudflareDnsSync();
    var done = new TaskCompletionSource<int>();
    var request = sync.SyncAsync("203.0.113.1", () => done.Task);
    sync.Invalidate();
    done.SetResult(1);
    await request;
    Check(sync.NeedsUpdate("203.0.113.1"), "The old request marked new settings as synced.");
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
