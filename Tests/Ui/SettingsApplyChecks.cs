using System.Drawing;
using System.Windows.Forms;

internal static partial class UiChecks
{
    private static void SettingsApplyChecks()
    {
        using var fixture = new Fixture();
        var running = fixture.Settings;
        foreach (var plugin in WidgetPlugins.All) running.SetPluginEnabled(plugin.Key, false);
        fixture.Widget.ApplySettings(running);
        var commits = 0;
        AppSettings? saved = null;
        void Commit(AppSettings candidate)
        {
            commits++;
            saved = candidate.Clone();
            fixture.Widget.ApplySettings(saved);
        }
        using (var window = new SettingsWindow(running, previewAppearance: fixture.Widget.PreviewAppearance, applySettings: Commit))
        {
            window.Show();
            Application.DoEvents();
            var apply = Get<Button>(window, "_apply");
            Call(window, "RefreshDirtyState");
            Check(!apply.Enabled, "Apply is enabled without edits.");
            var general = Get<SettingsPage[]>(window, "_commonPages").OfType<GeneralPage>().Single();
            var options = Get<AppearanceOptions>(general, "_appearance");
            var preset = Get<ComboBox>(options, "_preset");
            preset.SelectedIndex = (int)ColorPreset.Violet;
            Get<NumericUpDown>(options, "_size").Value = 110;
            Application.DoEvents();
            Call(window, "RefreshDirtyState");
            Check(apply.Enabled && commits == 0, "Editing did not enable Apply or saved prematurely.");
            var body = Get<FlowLayoutPanel>(general, "Body");
            var scroller = (ScrollableControl)body.Parent!;
            scroller.AutoScrollPosition = new Point(0, 90);
            var scroll = scroller.AutoScrollPosition;
            var pageIndex = Get<int>(window, "_index");
            apply.PerformClick();
            Application.DoEvents();
            Check(window.Visible && !window.IsDisposed && window.DialogResult == DialogResult.None,
                "Apply closed the editor.");
            Check(Get<int>(window, "_index") == pageIndex && scroller.AutoScrollPosition == scroll,
                "Apply changed the current page or scroll position.");
            Check(commits == 1 && saved!.ColorPreset == ColorPreset.Violet && saved.WidgetTextSize == 110
                && !apply.Enabled && Get<Label>(window, "_saveStatus").Text.Length > 0,
                "Apply did not commit once, clear dirty state, and show confirmation.");
            preset.SelectedIndex = (int)ColorPreset.Forest;
            Application.DoEvents();
            Call(window, "RefreshDirtyState");
            Check(apply.Enabled && Get<Palette>(fixture.Widget, "_p") == Palette.For(new AppSettings
                { Theme = running.Theme, ColorPreset = ColorPreset.Forest }), "Second edit was not live or dirty.");
            preset.SelectedIndex = (int)ColorPreset.Violet;
            Call(window, "RefreshDirtyState");
            Check(!apply.Enabled, "Returning to the applied value left Apply enabled.");
            preset.SelectedIndex = (int)ColorPreset.Forest;
            Application.DoEvents();
            ((Button)window.CancelButton!).PerformClick();
            window.Close();
            Application.DoEvents();
            Check(commits == 1 && Get<Palette>(fixture.Widget, "_p") == Palette.For(saved!),
                "Cancel reverted an applied choice or committed a later preview.");
        }
        using (var window = new SettingsWindow(saved!, applySettings: Commit))
        {
            window.Show(); Application.DoEvents();
            var general = Get<SettingsPage[]>(window, "_commonPages").OfType<GeneralPage>().Single();
            Get<CheckBox>(general, "_topMost").Checked = !saved!.WidgetTopMost;
            ((Button)window.AcceptButton!).PerformClick();
            Check(window.DialogResult == DialogResult.OK && !window.Visible && commits == 2,
                "OK failed to save once and close.");
        }
        using (var window = new SettingsWindow(saved!, applySettings: Commit))
        {
            window.Show(); Application.DoEvents();
            // An editor the change detection does not see must still be saved by OK
            ((Button)window.AcceptButton!).PerformClick();
            Check(window.DialogResult == DialogResult.OK && commits == 3, "OK without detected edits did not save.");
        }
        var withPlugins = saved!.Clone();
        withPlugins.SetPluginEnabled("price", true);
        withPlugins.SetPluginEnabled("proxmox", true);
        withPlugins.ProxmoxUrl = "https://pve.test:8006";
        withPlugins.ProxmoxCertThumbprint = "AABBCCDDEEFF00112233445566778899";
        using (var window = new SettingsWindow(withPlugins, applySettings: Commit))
        {
            window.Show(); Application.DoEvents();
            var apply = Get<Button>(window, "_apply");
            var pages = Get<SettingsPage[]>(window, "_pages");
            var history = pages.OfType<PriceHistoryPage>().Single();
            var proxmox = pages.OfType<ProxmoxPage>().Single();
            Call(window, "RefreshDirtyState");
            Check(!apply.Enabled, "Apply is enabled before the certificate is forgotten.");
            // A deferred edit that lives outside the page's controls must still enable Apply
            Call(Get<Button>(proxmox, "_forgetCert"), "OnClick", EventArgs.Empty);
            Call(window, "RefreshDirtyState");
            Check(apply.Enabled, "Forgetting the Proxmox certificate did not enable Apply.");
            apply.PerformClick(); Application.DoEvents();
            // Pages that keep the draft (history, appliances, rules) must see what was applied
            var draft = Get<AppSettings>(history, "_settings");
            Check(commits == 4 && saved!.ProxmoxCertThumbprint == null && ReferenceEquals(draft, window.Settings)
                && draft.ProxmoxCertThumbprint == null && !ReferenceEquals(draft, saved),
                "After Apply a page kept the old draft, or the draft is shared with the running settings.");
            window.Close();
        }
        Console.WriteLine("PASS Settings Apply: disabled until edited; commits once without closing, jumping or scrolling; undo clears dirty state; Cancel keeps last Apply; OK saves and closes");
    }
}
