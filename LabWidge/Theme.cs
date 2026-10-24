using System.Drawing;
using Microsoft.Win32;

/// <summary>Colours for the widget in the light and dark theme.</summary>
internal sealed record Palette(
    bool IsDark,
    Color Bg, Color Line, Color Track, Color HoverBg,
    Color TextPrimary, Color TextSecondary, Color TextDim,
    Color Blue, Color Green, Color Amber, Color Red)
{
    public static readonly Palette Dark = new(true,
        Color.FromArgb(19, 23, 30), Color.FromArgb(40, 46, 56), Color.FromArgb(42, 48, 58), Color.FromArgb(34, 40, 50),
        Color.FromArgb(230, 237, 243), Color.FromArgb(145, 154, 164), Color.FromArgb(110, 118, 129),
        Color.FromArgb(88, 166, 255), Color.FromArgb(63, 185, 80), Color.FromArgb(227, 179, 65), Color.FromArgb(248, 81, 73));

    public static readonly Palette Light = new(false,
        Color.FromArgb(250, 251, 252), Color.FromArgb(222, 226, 231), Color.FromArgb(228, 232, 237), Color.FromArgb(236, 240, 245),
        Color.FromArgb(31, 35, 40), Color.FromArgb(87, 96, 106), Color.FromArgb(125, 133, 144),
        Color.FromArgb(9, 105, 218), Color.FromArgb(26, 127, 55), Color.FromArgb(166, 112, 0), Color.FromArgb(207, 34, 46));

    public Color AccentText
    {
        get
        {
            static double Linear(byte channel)
            {
                var value = channel / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            var luminance = 0.2126 * Linear(Blue.R) + 0.7152 * Linear(Blue.G) + 0.0722 * Linear(Blue.B);
            return luminance > 0.179 ? Color.Black : Color.White;
        }
    }

    public Color Level(PriceLevel level) => level switch
    {
        PriceLevel.Cheap => Green,
        PriceLevel.Medium => Amber,
        _ => Red
    };

    public static Palette For(WidgetTheme theme) => theme switch
    {
        WidgetTheme.Light => Light,
        WidgetTheme.Dark => Dark,
        _ => WindowsUsesLightTheme() ? Light : Dark
    };

    public static Palette For(AppSettings settings)
    {
        var p = For(settings.Theme);
        var accent = settings.ColorPreset switch
        {
            ColorPreset.Ocean => p.IsDark ? Color.FromArgb(61, 201, 222) : Color.FromArgb(0, 112, 131),
            ColorPreset.Forest => p.IsDark ? Color.FromArgb(112, 205, 156) : Color.FromArgb(24, 117, 74),
            ColorPreset.Amber => p.IsDark ? Color.FromArgb(245, 190, 79) : Color.FromArgb(151, 96, 0),
            ColorPreset.Rose => p.IsDark ? Color.FromArgb(246, 145, 181) : Color.FromArgb(177, 50, 98),
            ColorPreset.Violet => p.IsDark ? Color.FromArgb(185, 157, 255) : Color.FromArgb(112, 71, 190),
            ColorPreset.Slate => p.IsDark ? Color.FromArgb(172, 190, 212) : Color.FromArgb(76, 100, 127),
            _ => p.Blue
        };
        if (settings.ColorPreset != ColorPreset.Standard && Enum.IsDefined(settings.ColorPreset))
        {
            p = p with { Blue = accent, Bg = Mix(p.Bg, accent, 0.05f),
                HoverBg = Mix(p.Bg, accent, p.IsDark ? 0.18f : 0.10f),
                Track = Mix(p.Track, accent, 0.18f), Line = Mix(p.Line, accent, 0.12f) };
        }
        if (settings.HighContrastText)
            p = p with { TextSecondary = p.IsDark ? Color.FromArgb(200, 208, 217) : Color.FromArgb(52, 60, 70),
                TextDim = p.IsDark ? Color.FromArgb(174, 184, 196) : Color.FromArgb(70, 79, 91) };
        return p;
    }

    private static Color Mix(Color a, Color b, float fraction) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * fraction),
        (int)Math.Round(a.G + (b.G - a.G) * fraction),
        (int)Math.Round(a.B + (b.B - a.B) * fraction));

    public static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }
}
