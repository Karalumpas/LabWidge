using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

internal static partial class UiChecks
{
    private static void LiveAppearanceChecks()
    {
        using var fixture = new Fixture();
        var widget = fixture.Widget;
        var original = fixture.Settings;
        var baseline = Palette.For(original);
        var draft = original.Clone();
        foreach (var plugin in WidgetPlugins.All) draft.SetPluginEnabled(plugin.Key, false);
        SectionWindows.OpenPinnedAt("network", new Point(60, 60));
        var windows = (IDictionary)typeof(SectionWindows).GetField("Open", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var panel = (PopupPanel)windows["network"]!;
        var saves = fixture.SaveCount;
        foreach (var close in new[] { "cancel", "close", "dispose", "save" })
        {
            using var window = new SettingsWindow(draft, previewAppearance: widget.PreviewAppearance);
            window.Show();
            Application.DoEvents();
            var pages = Get<SettingsPage[]>(window, "_commonPages");
            var general = pages.OfType<GeneralPage>().Single();
            var options = Get<AppearanceOptions>(general, "_appearance");
            Get<ComboBox>(options, "_preset").SelectedIndex = (int)ColorPreset.Violet;
            Get<ComboBox>(options, "_font").SelectedItem = AppearanceFonts.Resolve("Arial");
            Get<NumericUpDown>(options, "_size").Value = 120;
            Get<CheckBox>(options, "_bold").Checked = true;
            Get<TrackBar>(general, "_opacity").Value = 65;
            Application.DoEvents();
            var expected = original.Clone();
            expected.ColorPreset = ColorPreset.Violet;
            expected.WidgetFontFamily = AppearanceFonts.Resolve("Arial");
            expected.WidgetTextSize = 120;
            expected.WidgetBoldText = true;
            Check(Get<Palette>(widget, "_p") == Palette.For(expected), "Live preset did not reach the widget.");
            var fonts = Get<object>(widget, "_f");
            var body = (Font)fonts.GetType().GetField("Body")!.GetValue(fonts)!;
            Check(body.Bold && body.Name == expected.WidgetFontFamily, "Live font did not reach the widget.");
            Check(Math.Abs(Get<double>(widget, "_opacity") - .65) < .001, "Live opacity did not reach the widget.");
            Check((Palette)typeof(PopupPanel).GetProperty("P", Private)!.GetValue(panel)! == Palette.For(expected),
                "Live preset did not reach an open pinned window.");
            Check(ReferenceEquals(Get<AppSettings>(widget, "_settings"), original)
                && ReferenceEquals(SectionWindows.Settings, original) && original.ColorPreset == ColorPreset.Standard
                && original.WidgetTextSize == 100 && fixture.SaveCount == saves,
                "Preview mutated or saved the running settings.");
            if (close == "cancel") { ((Button)window.CancelButton!).PerformClick(); window.Close(); }
            else if (close == "dispose") window.Dispose();
            else if (close == "save") Call(window, "SaveAndClose");
            else window.Close();
            Application.DoEvents();
            Check(Get<Palette>(widget, "_p") == baseline && Get<double>(widget, "_opacity") == 1,
                "Closing the editor left temporary appearance behind.");
            Check((Palette)typeof(PopupPanel).GetProperty("P", Private)!.GetValue(panel)! == baseline,
                "Closing the editor did not restore pinned window appearance.");
            if (close == "save")
            {
                Check(window.DialogResult == DialogResult.OK && window.Settings.ColorPreset == ColorPreset.Violet
                    && window.Settings.WidgetTextSize == 120 && window.Settings.WidgetOpacity == 65,
                    "Save did not retain the previewed choices.");
                widget.ApplySettings(window.Settings);
                Check(Get<Palette>(widget, "_p") == Palette.For(expected), "Saved appearance did not apply.");
            }
        }
        SectionWindows.CloseAllForShutdown();
        Console.WriteLine("PASS Live appearance: preset/font/size/bold/opacity reach widget and pinned window; no settings writes; Cancel, close, disposal restore; Save retains choices");
    }
}
