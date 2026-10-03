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
