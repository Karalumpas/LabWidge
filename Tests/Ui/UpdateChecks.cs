using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

internal static partial class UiChecks
{
    /// <summary>An update source that answers what the test says, without GitHub or Microsoft Store.</summary>
    private sealed class FakeUpdates(UpdateChannel channel) : IUpdateSource
    {
        public UpdateChannel Channel => channel;
        public Func<UpdateOffer?> Check { get; set; } = () => null;
        public Func<bool> Install { get; set; } = () => true;
        public int Checks { get; private set; }
        public int Installs { get; private set; }

        public Task<UpdateOffer?> CheckAsync(CancellationToken cancel) { Checks++; return Task.FromResult(Check()); }

        public Task<bool> InstallAsync(UpdateOffer offer, IWin32Window owner, IProgress<double> progress, CancellationToken cancel)
        {
            Installs++;
            progress.Report(0.5);
            var ok = Install();
            if (ok && channel == UpdateChannel.GitHub) AppUpdates.PendingInstaller = Path.Combine(Path.GetTempPath(), "LabWidge-Setup-test.exe");
            return Task.FromResult(ok);
        }
    }

    private static void UpdateChecks()
    {
        var original = AppUpdates.Factory;
        try
        {
            using var fixture = new Fixture();
            var settings = fixture.Settings;
            var newer = new UpdateOffer(new Version(UpdateService.Current.Major, UpdateService.Current.Minor + 1, 0), "Docker plugin and more.", 7_400_000, "payload");

            foreach (var channel in new[] { UpdateChannel.Store, UpdateChannel.GitHub })
            {
                var fake = new FakeUpdates(channel);
                AppUpdates.Factory = () => fake;
                AppUpdates.PendingInstaller = null;

                // "Check for updates" in the tray menu: About opens and the check starts by itself
                using (var window = new SettingsWindow(settings, SettingsWindow.AboutKey) { CheckForUpdatesOnShow = true })
                {
                    window.Show();
                    var about = Get<SettingsPage[]>(window, "_pages").OfType<AboutPage>().Single();
                    PumpUntil(() => fake.Checks == 1 && about.Updates.StatusText.Contains(UpdateService.Current.ToString()) && about.Updates.StatusText.StartsWith("✓"));
                    Check(Get<SettingsPage[]>(window, "_pages")[Get<int>(window, "_index")] is AboutPage, "Check for updates did not open About.");
                    Check(about.Updates.Channel == channel, "The update panel uses the wrong source.");
                    Call(window, "RefreshDirtyState");
                    Check(!Get<Button>(window, "_apply").Enabled, "Checking for updates marked the settings as changed.");
                    Check(!Get<Button>(about.Updates, "_install").Visible, "An install button is shown without a newer version.");
                    Check(Get<LinkLabel>(about.Updates, "_storePage").Visible == (channel == UpdateChannel.Store), "The Microsoft Store link is shown for the wrong version.");

                    // A failed check says so and can be retried
                    fake.Check = () => throw new HttpRequestException("offline");
                    var failed = about.Updates.CheckAsync();
                    PumpUntil(() => failed.IsCompleted);
                    Check(about.Updates.StatusText.Contains("offline") && Get<Button>(about.Updates, "_check").Enabled, "A failed check gave no error or blocked a retry.");

                    // A newer version: one button installs it
                    fake.Check = () => newer;
                    var found = about.Updates.CheckAsync();
                    PumpUntil(() => found.IsCompleted);
                    var install = Get<Button>(about.Updates, "_install");
                    Check(install.Visible && install.Text.Contains(newer.Version.ToString()) && about.Updates.StatusText.Contains("Docker plugin"),
                        "A newer version did not offer to install it with its notes.");
                    LayoutProblems.Clear();
                    SettingsPageLayout(about, $"About with update ({channel})");
                    Check(LayoutProblems.Count == 0, "About layout:\n" + string.Join("\n", LayoutProblems.Distinct()));
                    using (var bitmap = new Bitmap(window.Width, window.Height))
                    {
                        window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
                        Directory.CreateDirectory(Path.Combine(Output, "settings"));
                        bitmap.Save(Path.Combine(Output, "settings", $"about-update-{channel.ToString().ToLowerInvariant()}.png"), ImageFormat.Png);
                    }

                    // Cancelled in the Store's own dialog: nothing happens
                    fake.Install = () => false;
                    install.PerformClick();
                    PumpUntil(() => fake.Installs == 1 && install.Enabled);
                    Check(window.Visible && AppUpdates.PendingInstaller == null, "A cancelled update closed Settings or queued an installer.");

                    fake.Install = () => true;
                    install.PerformClick();
                    PumpUntil(() => fake.Installs == 2 && (channel == UpdateChannel.GitHub ? !window.Visible : install.Enabled));
                    if (channel == UpdateChannel.GitHub)
                        Check(AppUpdates.PendingInstaller != null && !window.Visible, "The GitHub installer was not queued for when Settings closes.");
                    else
                        Check(window.Visible && AppUpdates.PendingInstaller == null && about.Updates.StatusText.Contains("Microsoft Store"),
                            "The Store update did not report that the Store installs it.");
                }
            }
            Console.WriteLine("PASS Updates: Check for updates opens About for Store and GitHub; latest, errors, a newer version, cancel and install behave");
        }
        finally
        {
            AppUpdates.Factory = original;
            AppUpdates.PendingInstaller = null;
        }
    }

    /// <summary>The settings menu's header is a box of its own: pages scrolled up never show behind the logo.</summary>
    private static void SettingsMenuHeader()
    {
        using var fixture = new Fixture();
        var s = fixture.Settings.Clone();
        foreach (var plugin in WidgetPlugins.All) s.SetPluginEnabled(plugin.Key, true);
        using var window = new SettingsWindow(s);
        window.Show();
        Application.DoEvents();
        var menu = Get<Control>(window, "_menu");
        Bitmap Shot()
        {
            var bitmap = new Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, menu.Size));
            return bitmap;
        }
        using var top = Shot();
        var header = (int)(76 * menu.DeviceDpi / 96f) - 1; // the divider sits on the header's last line once scrolled
        foreach (var scroll in new[] { 40f, 120f, 400f })
        {
            menu.GetType().GetField("_scroll", Private)!.SetValue(menu, scroll);
            menu.Invalidate();
            using var scrolled = Shot();
            Check(Equal(top, scrolled, 0, header), $"Menu items show behind the logo when scrolled by {scroll}.");
            if (scroll == 120f) scrolled.Save(Path.Combine(Output, "settings", "menu-scrolled.png"), ImageFormat.Png);
        }
        window.Close();
        Console.WriteLine("PASS UI: the settings menu keeps its header clear while the pages scroll");
    }
}
