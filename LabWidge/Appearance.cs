using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

/// <summary>Shared typography for the widget and detached section windows.</summary>
internal static class AppearanceFonts
{
    private static readonly Lazy<string[]> Installed = new(() =>
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Select(f => f.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToArray();
    });
    internal static string[] Families => Installed.Value;
    internal static string Resolve(string? family) => Families.FirstOrDefault(n =>
        string.Equals(n, family, StringComparison.OrdinalIgnoreCase)) ?? "Segoe UI";

    internal static Font Create(AppSettings settings, float pixels, bool emphasis = false, bool large = false)
    {
        try { return CreateCore(settings, pixels, emphasis, large); }
        catch (ArgumentException)
        {
            // A font with no usable style, or one removed since it was chosen: fall back rather than fail every paint
            var size = pixels * Math.Clamp(settings.WidgetTextSize, 90, 120) / 100f;
            return new Font("Segoe UI", size, emphasis || settings.WidgetBoldText ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }
    }

    private static Font CreateCore(AppSettings settings, float pixels, bool emphasis, bool large)
    {
        var family = Resolve(settings.WidgetFontFamily);
        var size = pixels * Math.Clamp(settings.WidgetTextSize, 90, 120) / 100f;
        // Keep the original default typography, including Segoe UI Semibold.
        if (family == "Segoe UI" && emphasis && !settings.WidgetBoldText && !large)
            return new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Pixel);
        var style = emphasis || settings.WidgetBoldText ? FontStyle.Bold : FontStyle.Regular;
        using var installed = new FontFamily(family);
        if (!installed.IsStyleAvailable(style))
            style = installed.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular
                : installed.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Italic;
        return new Font(installed, size, style, GraphicsUnit.Pixel);
    }
}

/// <summary>Edits a draft only; the running widget changes when Settings is saved.</summary>
internal sealed class AppearanceOptions : FlowLayoutPanel
{
    private readonly ComboBox _preset = new() { Width = Ui.FieldWidth, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _font = new() { Width = Ui.FieldWidth, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _size = new() { Minimum = 90, Maximum = 120, Increment = 5, Width = 80, Value = 100 };
    private readonly CheckBox _bold = Ui.Check(L.T("Bold text", "Fed tekst"));
    private readonly CheckBox _contrast = Ui.Check(L.T("Higher text contrast", "Højere tekstkontrast"));
    private readonly Preview _preview = new();
    private WidgetTheme _theme;
    internal event Action? Changed;

    internal AppearanceOptions()
    {
        AutoSize = true;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        Margin = Padding.Empty;
        _preset.Items.AddRange(new object[] { L.T("Standard", "Standard"), L.T("Ocean", "Hav"),
            L.T("Forest", "Skov"), L.T("Amber", "Rav"), L.T("Rose", "Rose"), L.T("Violet", "Violet"), L.T("Slate", "Skifer") });
        _font.Items.AddRange(AppearanceFonts.Families.Cast<object>().ToArray());
        var reset = new Button { AutoSize = true, Text = L.T("Reset colours and text", "Nulstil farver og tekst") };
        reset.Click += (_, _) => LoadFrom(new AppSettings { Theme = _theme });
        Controls.AddRange(new Control[] {
            Ui.Row(L.T("Colour preset", "Farvepreset"), _preset),
            Ui.Row(L.T("Font", "Skrifttype"), _font),
            Ui.Row(L.T("Text size", "Tekststørrelse"), _size, Ui.Inline("%")),
            _bold, _contrast,
            Ui.Help(L.T("Applies to the widget and section windows. Colours also apply to Settings. Changes are previewed live. Apply or OK saves them; Cancel restores your last saved appearance.",
                "Gælder widgetten og sektionsvinduer. Farver gælder også indstillingerne. Ændringer vises live. Anvend eller OK gemmer dem; Annuller gendanner det senest gemte udseende.")),
            _preview, reset });
        _preset.SelectedIndexChanged += (_, _) => UpdatePreview();
        _font.SelectedIndexChanged += (_, _) => UpdatePreview();
        _size.ValueChanged += (_, _) => UpdatePreview();
        _bold.CheckedChanged += (_, _) => UpdatePreview();
        _contrast.CheckedChanged += (_, _) => UpdatePreview();
    }

    internal void SetTheme(WidgetTheme theme) { _theme = theme; UpdatePreview(); }
    internal void LoadFrom(AppSettings settings)
    {
        _theme = settings.Theme;
        _preset.SelectedIndex = Enum.IsDefined(settings.ColorPreset) ? (int)settings.ColorPreset : 0;
        _font.SelectedItem = AppearanceFonts.Resolve(settings.WidgetFontFamily);
        _size.Value = Math.Clamp(settings.WidgetTextSize, 90, 120);
        _bold.Checked = settings.WidgetBoldText;
        _contrast.Checked = settings.HighContrastText;
        UpdatePreview();
    }
    internal void SaveTo(AppSettings settings)
    {
        settings.ColorPreset = (ColorPreset)Math.Max(0, _preset.SelectedIndex);
        settings.WidgetFontFamily = _font.SelectedItem as string ?? "Segoe UI";
        settings.WidgetTextSize = (int)_size.Value;
        settings.WidgetBoldText = _bold.Checked;
        settings.HighContrastText = _contrast.Checked;
    }
    private void UpdatePreview()
    {
        var settings = new AppSettings { Theme = _theme };
        SaveTo(settings);
        _preview.Settings = settings;
        _preview.Invalidate();
        Changed?.Invoke();
    }

    private sealed class Preview : Control
    {
        internal AppSettings Settings = new();
        internal Preview()
        {
            Size = new Size(Ui.ContentWidth, 112);
            Margin = new Padding(0, 4, 0, 10);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            AccessibleName = L.T("Appearance preview", "Forhåndsvisning af udseende");
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Palette.For(Settings);
            e.Graphics.Clear(p.Bg);
            var scale = DeviceDpi / 96f;
            using var font = AppearanceFonts.Create(Settings, 12.5f * scale);
            using var title = AppearanceFonts.Create(Settings, 12.5f * scale, true);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            void Line(string text, Font f, Color c, int y) => TextRenderer.DrawText(e.Graphics, text, f,
                new Rectangle((int)(12 * scale), (int)(y * scale), Width - (int)(24 * scale), (int)(24 * scale)), c, flags);
            Line(L.T("LabWidge · Appearance preview", "LabWidge · Forhåndsvisning"), title, p.Blue, 10);
            Line(L.T("Primary text · CPU 24 % · 1.25 kr./kWh", "Primær tekst · CPU 24 % · 1,25 kr./kWh"), font, p.TextPrimary, 36);
            Line(L.T("Secondary text · Updated just now", "Sekundær tekst · Opdateret lige nu"), font, p.TextSecondary, 60);
            Line(L.T("Details · æøå ÆØÅ · 0123456789", "Detaljer · æøå ÆØÅ · 0123456789"), font, p.TextDim, 84);
            using var border = new Pen(p.Line);
            e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }
}
