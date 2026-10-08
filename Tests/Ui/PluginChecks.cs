using System.Drawing;

internal static partial class UiChecks
{
    private static void PluginChecks()
    {
        using var fixture = new Fixture();
        var s = fixture.Settings;
        var widget = fixture.Widget;
        Check(WidgetPlugins.All.Select(p => p.Key).Distinct().Count() == 7, "Plugin registration must contain seven unique sections.");

        // Private DNS is intentional; rendering and refreshing must not offer to replace it.
        foreach (var plugin in WidgetPlugins.All) s.SetPluginEnabled(plugin.Key, false);
        s.SetPluginEnabled("cloudflare", true);
        s.CollapsedCloudflare = false;
        var cf = Get<CloudflareService>(widget, "_cf");
        Set(cf, "ARecords", new List<CloudflareRecord> { new() { Name = "seer.example.test", Content = "192.168.0.25" } });
        widget.ApplySettings(s);
        using (var frame = Frame(widget, "cloudflare-read-only.png")) { }
        var tips = Hits(widget).Select(h => h.Tip ?? "").ToList();
        Check(tips.Any(t => t.Contains("read-only") || t.Contains("kun læsning")), "DNS has no read-only explanation.");
        Check(!tips.Any(t => t.Contains("old IP") || t.Contains("gammel IP") || t.Contains("current IP now") || t.Contains("nuværende IP nu")),
              "A private address still offers DNS replacement or reports an old IP.");
        foreach (var plugin in WidgetPlugins.All) s.SetPluginEnabled(plugin.Key, true);
        Console.WriteLine("PASS Cloudflare: private DNS is read-only and has no replacement actions");

        // Legacy configuration remains active without an explicit plugin map; hidden Cloudflare still monitors status.
        s.ShowCloudflare = false;
        Check(s.IsPluginEnabled("cloudflare") && s.HasCloudflare, "Migration disabled a configured background integration.");
        s.ShowCloudflare = true;
        foreach (var plugin in WidgetPlugins.All) s.SetPluginEnabled(plugin.Key, false);
        widget.ApplySettings(s);
        using (var empty = Frame(widget, "plugins-disabled.png")) { }
        Check(!Get<IEnumerable<object>>(widget, "_sectionBounds").Any(), "Disabled plugins still render in the widget.");
        foreach (var plugin in WidgetPlugins.All)
        {
            SectionWindows.Toggle(plugin.Key, widget.Bounds);
            Check(!SectionWindows.IsOpen(plugin.Key), "A disabled plugin opened a window.");
        }

        s.SetPluginEnabled("network", true);
        s.ShowNetwork = true;
        widget.ApplySettings(s);
        using (var frame = Frame(widget, "plugins-network-only.png")) { }
        Check(Get<IEnumerable<object>>(widget, "_sectionBounds").Count() == 1, "Only the enabled plugin should render.");

        SectionWindows.OpenPinnedAt("network", Screen.PrimaryScreen!.WorkingArea.Location + new Size(40, 40));
        Application.DoEvents();
        var pinned = OpenWindow("network");
        var savedBounds = pinned.Bounds;
        s.SetPluginEnabled("network", false);
        widget.ApplySettings(s);
        Check(pinned.IsDisposed && s.SectionWindows["network"].Pinned && s.SectionWindows["network"].Open,
            "Deactivation must close the window while preserving its pinned state.");
        s.SetPluginEnabled("network", true);
        widget.ApplySettings(s);
        SectionWindows.RestorePinned(s);
        Application.DoEvents();
        Check(SectionWindows.IsOpen("network") && OpenWindow("network").Location == savedBounds.Location,
            "Reactivation did not restore the saved pinned window.");
        SectionWindows.CloseAllForShutdown();
        Console.WriteLine("PASS Plugins: legacy preferences, disabled rendering/windows, and pinned reactivation");

        using (var settings = new SettingsWindow(s))
        {
            var page = Get<PluginsPage>(settings, "_pluginsPage");
            var switches = Get<Dictionary<string, CheckBox>>(page, "_switches");
            var networkPage = Get<Dictionary<string, SettingsPage>>(settings, "_pluginPages")["network"];
            Get<TextBox>(networkPage, "_ping").Text = "router.example.test";
            switches["network"].Checked = false;
            Check(!Get<SettingsPage[]>(settings, "_pages").Contains(networkPage), "Inactive plugin settings remain in the menu.");
            Check(s.IsPluginEnabled("network"), "Unsaved plugin changes escaped the settings draft.");
            switches["network"].Checked = true;
            Check(Get<SettingsPage[]>(settings, "_pages").Contains(networkPage), "Reactivation lost the settings page.");
            Check(Get<TextBox>(networkPage, "_ping").Text == "router.example.test", "Toggling activation lost unsaved plugin configuration.");
            switches["network"].Checked = false;
            Call(settings, "SaveAndClose");
            Check(settings.DialogResult == DialogResult.OK && !settings.Settings.IsPluginEnabled("network"), "Saving all-disabled plugins failed.");
            Check(settings.Settings.PingTarget == s.PingTarget, "A disabled page overwrote preserved settings.");
        }
        Console.WriteLine("PASS Plugins: dynamic settings pages, draft isolation, retained edits, and all-disabled save");

        var starts = 0;
        var stopped = 0;
        var active = 0;
        var maxActive = 0;
        var unblock = new TaskCompletionSource();
        var testPlugin = new ProbePlugin("probe", async ct =>
        {
            starts++; active++; maxActive = Math.Max(maxActive, active);
            try { await unblock.Task; ct.ThrowIfCancellationRequested(); }
            finally { stopped++; active--; }
        });
        using (var runtime = new PluginRuntime(new[] { testPlugin }))
        {
            var settings = new AppSettings();
            runtime.ApplySettings(widget.Services, settings);
            Check(starts == 0, "An inactive plugin started polling.");
            settings.SetPluginEnabled("probe", true);
            runtime.ApplySettings(widget.Services, settings);
            Check(starts == 1, "Activation did not start the plugin.");
            var previous = runtime.TokenFor("probe");
            settings.SetPluginEnabled("probe", false);
            runtime.ApplySettings(widget.Services, settings);
            Check(previous.IsCancellationRequested, "Deactivation did not cancel in-flight work.");
            settings.SetPluginEnabled("probe", true);
            runtime.ApplySettings(widget.Services, settings);
            Check(starts == 1, "Reactivation overlapped a draining plugin generation.");
            unblock.SetResult();
            PumpUntil(() => starts == 2 && stopped == 2);
            Check(maxActive == 1, "Plugin polling overlapped during rapid reactivation.");
            settings.SetPluginEnabled("probe", false);
            runtime.ApplySettings(widget.Services, settings);
            Thread.Sleep(150); Application.DoEvents();
            Check(starts == 2, "A disabled plugin's timer restarted polling.");
        }

        var healthyRuns = 0;
        using (var runtime = new PluginRuntime(new IWidgetPlugin[]
        {
            new ProbePlugin("broken", _ => throw new InvalidOperationException("Plugin isolation test")),
            new ProbePlugin("healthy", _ => { healthyRuns++; return Task.CompletedTask; })
        }))
        {
            var settings = new AppSettings(); settings.SetPluginEnabled("broken", true); settings.SetPluginEnabled("healthy", true);
            runtime.ApplySettings(widget.Services, settings);
            Check(healthyRuns == 1, "One plugin's failure stopped another plugin.");
        }
        Console.WriteLine("PASS Plugins: no inactive jobs, cancellation, no overlapping reactivation, stopped timers, and failure isolation");

        // Every enabled plugin supplies all three views, plus an independent settings page.
        foreach (var plugin in WidgetPlugins.All) s.SetPluginEnabled(plugin.Key, true);
        s.ShowSystem = s.ShowNetwork = s.ShowAudio = s.ShowPrice = true;
        s.CollapsedHomeAssistant = false;
        widget.ApplySettings(s);
        using (var bitmap = new Bitmap(640, 2000))
        using (var g = Graphics.FromImage(bitmap))
        {
            foreach (var plugin in WidgetPlugins.All)
            {
                Call(widget, "SetCollapsed", plugin.Key, false);
                var fullHeight = plugin.RenderExpanded(widget, g, 16, 0, 400);
                var compactHeight = plugin.RenderCompact(widget, g, 16, 0, 400);
                Check(fullHeight > compactHeight, $"The {plugin.Key} plugin has no distinct expanded view.");
            }
        }
        using var expanded = Frame(widget, "plugins-expanded.png");
        var visible = Get<IEnumerable<object>>(widget, "_sectionBounds").Count();
        widget.SetCompact(true);
        using var compact = Frame(widget, "plugins-compact.png");
        Check(compact.Height < expanded.Height, "Plugin compact views did not reduce the widget's height.");
        var hits = Get<IEnumerable<object>>(widget, "_hits");
        Check(hits.Any(), "Compact plugin views are not interactive.");
        widget.SetCompact(false);
        Check(visible == 7, "Not every plugin supplies an expanded view.");
        Console.WriteLine("PASS Plugins: all seven expanded and compact views remain interactive");
    }

    private static void PumpUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
        Check(done(), "Plugin lifecycle check timed out.");
    }

    private sealed class ProbePlugin(string key, Func<CancellationToken, Task> run) : WidgetPlugin
    {
        public override string Key => key;
        public override string Title => key;
        public override string Glyph => "";
        public override string Description => "";
        public override bool IsVisible(DashboardForm h, AppSettings s) => true;
        public override float RenderExpanded(DashboardForm h, Graphics g, float x, float y, float w) => y;
        public override PopupPanel? CreateWindow(DashboardForm h, AppSettings s, Action save) => null;
        public override SettingsPage CreateSettingsPage() => new NetworkPluginPage();
        public override IEnumerable<PluginJob> CreateJobs(PluginServices services, AppSettings settings)
        {
            yield return new("probe", 100, run);
        }
    }
}
