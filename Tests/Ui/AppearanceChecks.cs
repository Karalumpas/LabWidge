using System.Drawing;
using System.Windows.Forms;

internal static partial class UiChecks
{
    private static void AppearanceChecks()
    {
        var defaults = new AppSettings { Theme = WidgetTheme.Dark };
        Check(Palette.For(defaults) == Palette.Dark, "Default appearance changed existing colours.");
        using (var fonts = new PanelFonts(1, defaults))
        {
            Check(fonts.Body.Name == "Segoe UI" && fonts.Body.Size == 12.5f, "Default typography changed.");
            Check(fonts.BodyBold.Name == "Segoe UI Semibold", "Default emphasis changed.");
        }
        var draft = new AppSettings { Theme = WidgetTheme.Light, ColorPreset = ColorPreset.Violet,
            WidgetFontFamily = "Arial", WidgetTextSize = 120, WidgetBoldText = true, HighContrastText = true };
        var saved = draft.Clone();
        Check(saved.WidgetFontFamily == draft.WidgetFontFamily && saved.WidgetTextSize == 120
            && saved.ColorPreset == ColorPreset.Violet && saved.HighContrastText && saved.WidgetBoldText,
            "Appearance did not survive settings serialization.");
        using (var page = new GeneralPage())
        {
            page.LoadFrom(saved);
            var result = new AppSettings();
            Check(page.SaveTo(result) == null && result.ColorPreset == saved.ColorPreset
                && result.WidgetTextSize == 120 && result.WidgetBoldText && result.HighContrastText
                && result.WidgetFontFamily == AppearanceFonts.Resolve(saved.WidgetFontFamily), "Appearance page did not round-trip.");
            Check(saved.WidgetTextSize == 120, "Appearance editing changed the source settings.");
        }
        using (var fallback = AppearanceFonts.Create(new AppSettings { WidgetFontFamily = "Missing-font-123", WidgetTextSize = 9999 }, 10))
            Check(fallback.Name == "Segoe UI" && Math.Abs(fallback.Size - 12) < .01, "Font fallback or size bounds failed.");
        using var fixture = new Fixture();
        foreach (var theme in new[] { WidgetTheme.Dark, WidgetTheme.Light })
        foreach (var preset in Enum.GetValues<ColorPreset>())
        {
            var settings = fixture.Settings;
            settings.Theme = theme;
            settings.ColorPreset = preset;
            settings.WidgetTextSize = 120;
            settings.WidgetFontFamily = "Arial";
            settings.WidgetBoldText = true;
            settings.HighContrastText = true;
            var p = Palette.For(settings);
            Check(p.Green == Palette.For(theme).Green && p.Red == Palette.For(theme).Red,
                "A colour preset changed semantic status colours.");
            fixture.Widget.ApplySettings(settings);
            Call(fixture.Widget, "FitSize");
            using var frame = Frame(fixture.Widget, $"appearance/{theme}-{preset}.png");
            Check(frame.Width > 0 && frame.Height > 0, "Appearance produced an empty widget.");
        }
        using var panelFonts = new PanelFonts(1, draft);
        Check(Math.Abs(panelFonts.Body.Size - 15) < .01 && panelFonts.Body.Bold,
            "Section typography ignored size or bold preference.");
        Console.WriteLine("PASS Appearance: defaults, saved settings, editor round-trip, missing fonts, bounds, status colours and all 14 preset variants");
    }
}
