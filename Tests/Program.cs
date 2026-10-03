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

var tests = new (string Name, Func<Task> Run)[]
{
    ("DNS: empty and whitespace-only host lists make no requests", EmptyHosts),
    ("DNS: selected hosts and all hosts remain distinct", SelectedHosts),
    ("DNS: failed updates are retried and successful updates stop retrying", RetryDns),
    ("DNS: settings changed during a request remain pending", ChangedDnsSettings),
    ("Prices: concurrent settings changes are coalesced and applied", ChangedTariff),
    ("Installer: a complete directory replaces the previous version and keeps login", InstallSuccess),
    ("Installer: a failed switch restores all original files", InstallRollback),
    ("Installer: rollback keeps the prepared package available for retry", InstallRetry),
    ("Installer: interrupted switches recover the old version or finish the new one", InstallRecovery),
    ("Installer: incomplete packages leave the existing version intact", InvalidPackage),
    ("Uninstall: cleanup waits for process exit and handles quoted paths", UninstallWait),
    ("Installer: an inherited working directory is released before switching folders", InstallerWorkingDirectory),
    ("Widget: pinned bands reserve the middle viewport and clamp scroll", LayoutBands),
    ("Widget: status distinguishes fresh, stale and failed data", FreshnessStates),
    ("Widget: size and pin preferences survive settings cloning", LayoutSettings)
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
            return Json(new { records = new[] { new { TimeDK = DateTime.Today.ToString("yyyy-MM-dd'T'HH:mm:ss"), DayAheadPriceDKK = 1000 } } });
        }
        return Json(new { records = new[] { new
        {
            ChargeTypeCode = "C", Note = "", ValidFrom = "2020-01-01T00:00:00", ValidTo = (string?)null,
            Price1 = url.Contains("New") ? 1.0 : 0.5
        } } });
    }));
    var service = new ElectricityPriceService(http);
    var settings = new AppSettings { PriceArea = "DK1", NetTariffOwner = "Old", NetTariffCodes = new[] { "C" } };
    var running = service.RefreshAsync(settings);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    // Mutating the original object must not mix the old area with the new tariff.
    settings.PriceArea = "DK2";
    settings.NetTariffOwner = "New";
    for (var i = 0; i < 5; i++) await service.RefreshAsync(settings);
    resume.SetResult();
    await running;
    Check(service.FetchedArea == "DK2", "Area change was lost.");
    Check(service.NetTariffs.Single().HourlyOre[0] == 100, "Tariff change was lost.");
    Check(requests.Count(r => r.Contains("DayAheadPrices")) == 2, "Repeated refreshes were not coalesced.");
    Check(requests.First(r => r.Contains("ChargeOwner")).Contains("Old"), "In-flight settings were not snapshotted.");
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
