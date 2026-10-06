using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Net.Http;
using System.Windows.Forms;

/// <summary>Shared building blocks for the settings and the setup guide.</summary>
internal static class Ui
{
    /// <summary>The theme of the settings window being built – set before its pages are created.</summary>
    public static Palette Palette { get; set; } = Palette.Light;

    public static Color Accent => Palette.Blue;
    public static Color Muted => Palette.TextSecondary;
    public static Color Ok => Palette.Green;
    public static Color Warn => Palette.Amber;
    public static Color Error => Palette.Red;

    public const int ContentWidth = 470;

    public static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 15F),
        Margin = new Padding(0, 0, 0, 4)
    };

    /// <summary>The title of a group of settings; the page puts the group in a card.</summary>
    public static Label Section(string text) => new()
    {
        Text = text,
        Tag = SectionTag,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10.5F),
        Margin = new Padding(0, 0, 0, 6)
    };

    public const string SectionTag = "section";
    public const string HelpTag = "help";

    public static Label Help(string text, int width = ContentWidth) => new()
    {
        Text = text,
        Tag = HelpTag,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        ForeColor = Muted,
        Margin = new Padding(0, 0, 0, 8)
    };

    /// <summary>An on/off setting – drawn as a switch with the text on the left.</summary>
    public static CheckBox Check(string text) => new ToggleSwitch
    {
        Text = text,
        Margin = new Padding(0, 1, 0, 1)
    };

    /// <summary>Label on the left, control on the right.</summary>
    public static Control Row(string label, params Control[] inputs)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 3, 0, 3)
        };
        row.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            MinimumSize = new Size(170, 26),
            MaximumSize = new Size(170, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 4, 0)
        });
        foreach (var input in inputs)
        {
            input.Margin = new Padding(0, 2, 8, 0);
            row.Controls.Add(input);
        }
        return row;
    }

    public static Label Inline(string text, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = color ?? Muted,
        Padding = new Padding(0, 5, 0, 0)
    };
}

internal abstract class SettingsPage : UserControl
{
    protected readonly FlowLayoutPanel Body;

    protected SettingsPage()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Dock = DockStyle.Fill;
        Font = new Font("Segoe UI", 9.5F);
        BackColor = SettingsTheme.PageColor(Ui.Palette);
        ForeColor = Ui.Palette.TextPrimary;
        Body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(28, 22, 20, 16)
        };
        Controls.Add(Body);
    }

    public abstract string Title { get; }

    /// <summary>The page's icon in the settings menu (Segoe Fluent Icons).</summary>
    public virtual string Glyph => "\uE713";

    private Card? _card;

    /// <summary>
    /// Adds controls to the page. A section title starts a card that holds the controls after it; a heading ends it,
    /// so the page's own intro text stays outside the cards.
    /// </summary>
    protected void Add(params Control[] controls)
    {
        foreach (var c in controls)
        {
            if (IsHeading(c))
            {
                _card = null;
                Body.Controls.Add(c);
            }
            else if (c is Label { Tag: Ui.SectionTag })
            {
                _card = new Card();
                Body.Controls.Add(_card);
                _card.Controls.Add(c);
            }
            else if (_card == null && c is Label { Tag: Ui.HelpTag })
            {
                Body.Controls.Add(c); // the page's intro
            }
            else
            {
                if (_card == null)
                {
                    _card = new Card();
                    Body.Controls.Add(_card);
                }
                _card.Controls.Add(c);
            }
        }
    }

    private static bool IsHeading(Control c) => c is Label l && l.Font.Size >= 14;

    public abstract void LoadFrom(AppSettings s);

    /// <summary>Writes the values to the settings. Returns an error text if something is missing.</summary>
    public abstract string? SaveTo(AppSettings s);

    /// <summary>Called when the page is shown for the first time (e.g. to fetch data).</summary>
    public virtual Task OnFirstShownAsync() => Task.CompletedTask;
    public virtual void UsePluginActivation() { }
}

// ---------------------------------------------------------------------------

internal sealed class WelcomePage : SettingsPage
{
    public override string Title => L.T("Welcome", "Velkommen");
    public override string Glyph => "\uE80F";

    public WelcomePage()
    {
        var logo = new PictureBox
        {
            Image = WidgetIcon.Render(96, WidgetIcon.Amber),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(72, 72),
            Margin = new Padding(0, 0, 0, 12)
        };
        Add(logo,
            Ui.Heading(L.T("Welcome to LabWidge", "Velkommen til LabWidge")),
            Ui.Help(L.T("A small widget by the clock that keeps you up to date on:", "En lille widget ved uret, der holder dig opdateret om:")),
            Ui.Help(L.T("•  The electricity price right now in 14 European countries – with a chart and the cheapest hours\n" +
                        "•  A heads-up before power gets cheap or expensive\n" +
                        "•  CPU, RAM and disks\n" +
                        "•  Local and external IP address, ping and traffic\n" +
                        "•  Optional: control lights and switches through Home Assistant\n" +
                        "•  Optional: automatic Cloudflare DNS updates",
                        "•  Elprisen lige nu i 14 europæiske lande – med graf og de billigste timer\n" +
                        "•  Besked før strømmen bliver billig eller dyr\n" +
                        "•  CPU, RAM og diske\n" +
                        "•  Intern og ekstern IP-adresse, ping og trafik\n" +
                        "•  Valgfrit: styring af lys og kontakter via Home Assistant\n" +
                        "•  Valgfrit: automatisk opdatering af Cloudflare DNS")),
            Ui.Help(L.T("Setup takes a minute. You can always change everything under Settings in the tray icon's menu.",
                        "Opsætningen tager et minut. Du kan altid ændre alt under Indstillinger i menuen på tray-ikonet.")));
    }

    public override void LoadFrom(AppSettings s) { }
    public override string? SaveTo(AppSettings s) => null;
}

// ---------------------------------------------------------------------------

internal sealed class PricePage : SettingsPage
{
    private static readonly HttpClient Http = HttpClientFactory.Create(TimeSpan.FromSeconds(30));
    private static IReadOnlyList<string>? _companyCache;
    private static CultureInfo Fmt => L.Culture;
    private static readonly string OtherCountry = L.T("Other country (no electricity price)", "Andet land (ingen elpris)");

    private readonly ComboBox _country = new() { Width = 290, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _intro = Ui.Help("");
    private readonly ComboBox _area = new() { Width = 290, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _postal = new() { Width = 70, MaxLength = 4 };
    private readonly Label _postalHint = Ui.Inline("");
    private readonly RadioButton _dk1 = new() { Text = L.T("DK1 – Jutland and Funen", "DK1 – Jylland og Fyn"), AutoSize = true };
    private readonly RadioButton _dk2 = new() { Text = L.T("DK2 – Zealand, Lolland-Falster and Bornholm", "DK2 – Sjælland, Lolland-Falster og Bornholm"), AutoSize = true };
    private readonly CheckBox _spotOnly = Ui.Check(L.T("Show the spot price only (without grid tariff and taxes)", "Vis kun spotpris (uden nettarif og afgifter)"));
    private readonly ComboBox _company = new() { Width = 290, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    private readonly Label _companyStatus = Ui.Inline("");
    private readonly ComboBox _tariff = new() { Width = 290, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _tariffPreview = Ui.Help("", Ui.ContentWidth);
    private readonly TextBox _supplierName = new() { Width = 200, PlaceholderText = L.T("e.g. Aura Energi", "fx Aura Energi") };
    private readonly NumericUpDown _supplierAddOn = new() { Width = 80, DecimalPlaces = 2, Increment = 0.5m, Minimum = -100, Maximum = 500 };
    private readonly Label _addOnUnit = Ui.Inline("");
    private readonly Label _addOnHelp = Ui.Help("");
    private readonly NumericUpDown _vatPercent = new() { Width = 80, DecimalPlaces = 1, Increment = 0.5m, Minimum = 0, Maximum = 40 };
    private readonly CheckBox _showPrice = Ui.Check(L.T("Show the electricity price in the widget", "Vis elprisen i widgetten"));
    private readonly CheckBox _vat = Ui.Check(L.T("Show prices including VAT", "Vis priser inkl. moms"));
    private readonly Panel _totalPanel;
    private readonly Control _gridPanel, _areaRow, _postalRow, _dk1Row, _dk2Row, _displaySection, _vatRow;

    private TariffDetection? _detection;
    private string _loadedOwner = "";
    private string[] _loadedCodes = Array.Empty<string>();
    private int _detectVersion;
    private bool _companiesRequested;

    public override string Title => L.T("Electricity", "Elpris");
    public override string Glyph => "\uE945";

    private sealed record AreaItem(PriceArea Area)
    {
        public override string ToString() => Area.ShortName.Length > 0 ? $"{Area.Code} – {Area.ShortName}" : Area.Code;
    }

    private Country? SelectedCountry => _country.SelectedItem as Country;

    public PricePage()
    {
        _gridPanel = Stack(
            Ui.Section(L.T("Grid company", "Netselskab")),
            Ui.Help(L.T("The grid company owns the wires to your home – it is not necessarily your electricity supplier. " +
                        "You'll find it on your electricity bill under grid tariff (nettarif) or grid subscription.",
                        "Netselskabet ejer ledningerne til din bolig – det er ikke nødvendigvis dit elselskab. " +
                        "Det står på elregningen under nettarif eller netabonnement.")),
            Ui.Row(L.T("Grid company", "Netselskab"), _company),
            Ui.Row("", _companyStatus),
            Ui.Row(L.T("Tariff", "Tarif"), _tariff),
            _tariffPreview);
        _totalPanel = Stack(
            _gridPanel,
            Ui.Section(L.T("Electricity supplier", "Elselskab")),
            _addOnHelp,
            Ui.Row(L.T("Supplier (optional)", "Elselskab (valgfrit)"), _supplierName),
            Ui.Row(L.T("Add-on excl. VAT", "Tillæg ekskl. moms"), _supplierAddOn, _addOnUnit));

        _areaRow = Ui.Row(L.T("Price area", "Prisområde"), _area);
        _postalRow = Ui.Row(L.T("Postal code", "Postnummer"), _postal, _postalHint);
        _dk1Row = Ui.Row(L.T("Price area", "Prisområde"), _dk1);
        _dk2Row = Ui.Row("", _dk2);
        _vatRow = Ui.Row(L.T("VAT", "Moms"), _vatPercent, Ui.Inline("%"));
        _displaySection = Stack(Ui.Section(L.T("Display", "Visning")), _showPrice, _vat, _vatRow, _spotOnly, _totalPanel);

        _country.Items.AddRange(Countries.All.Cast<object>().ToArray());
        _country.Items.Add(OtherCountry);

        Add(Ui.Heading(L.T("Electricity price", "Elpris")),
            Ui.Row(L.T("Country", "Land"), _country),
            _intro,
            _areaRow,
            _postalRow,
            _dk1Row,
            _dk2Row,
            _displaySection);

        _country.SelectedIndexChanged += async (_, _) =>
        {
            var previous = _vatCountry;
            ApplyCountry();
            // A new country brings its usual VAT, unless the user had changed it (coming from Other there is nothing to keep)
            if (SelectedCountry is { } c && (previous == null || (double)_vatPercent.Value == previous.VatPercent))
                _vatPercent.Value = (decimal)c.VatPercent;
            _vatCountry = SelectedCountry;
            if (SelectedCountry?.Code == "DK") await EnsureCompaniesAsync();
        };
        _postal.TextChanged += (_, _) =>
        {
            var area = TariffCatalog.AreaFromPostalCode(_postal.Text);
            if (area == null)
            {
                _postalHint.Text = _postal.Text.Length == 4 ? L.T("unknown postal code", "ukendt postnummer") : "";
                return;
            }
            (area == "DK2" ? _dk2 : _dk1).Checked = true;
            _postalHint.Text = area == "DK2" ? L.T("→ East Denmark (DK2)", "→ Østdanmark (DK2)") : L.T("→ West Denmark (DK1)", "→ Vestdanmark (DK1)");
        };
        _spotOnly.CheckedChanged += (_, _) => _totalPanel.Enabled = !_spotOnly.Checked;
        _company.SelectionChangeCommitted += async (_, _) => await DetectAsync((_company.SelectedItem as string) ?? _company.Text);
        _company.Leave += async (_, _) =>
        {
            if (_company.Text.Trim().Length > 0 && _company.Text.Trim() != (_detection == null ? "" : _lastDetectedOwner))
                await DetectAsync(_company.Text.Trim());
        };
        _tariff.SelectedIndexChanged += (_, _) => UpdatePreview();
    }

    private Country? _vatCountry;

    private static Panel Stack(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        panel.Controls.AddRange(controls);
        return panel;
    }

    /// <summary>Shows the fields that fit the chosen country: Denmark has postal code, grid company and tariffs.</summary>
    private void ApplyCountry()
    {
        var country = SelectedCountry;
        var danish = country?.Code == "DK";
        _postalRow.Visible = _dk1Row.Visible = _dk2Row.Visible = _gridPanel.Visible = danish;
        _areaRow.Visible = country != null && !danish && country.Areas.Count > 1;
        _displaySection.Visible = country != null;
        _spotOnly.Text = danish
            ? L.T("Show the spot price only (without grid tariff and taxes)", "Vis kun spotpris (uden nettarif og afgifter)")
            : L.T("Show the spot price only (without add-on)", "Vis kun spotpris (uden tillæg)");

        if (country == null)
        {
            _intro.Text = L.T("Electricity prices are not available for your country, so the electricity section is hidden in the widget.",
                              "Elpriser findes ikke for dit land, så elpris-sektionen er skjult i widgetten.");
            return;
        }

        _intro.Text = danish
            ? L.T("Your Danish postal code tells us the price area, and Energinet's public data is used to calculate your price.",
                  "Vi bruger dit postnummer til at finde prisområdet og Energinets offentlige data til at beregne din pris.")
            : L.T("The widget shows the day-ahead spot price for your price area from free public sources. " +
                  "Add your supplier's add-on and other charges per kWh to see what you pay.",
                  "Widgetten viser day-ahead-spotprisen for dit prisområde fra gratis, offentlige kilder. " +
                  "Læg dit elselskabs tillæg og andre gebyrer pr. kWh til for at se, hvad du betaler.");
        _addOnUnit.Text = country.Unit.PerKwh;
        _addOnHelp.Text = danish
            ? L.T("The supplier's add-on per kWh is in your contract or on the bill (typically 0–10 øre excl. VAT).",
                  "Elselskabets tillæg pr. kWh står i din elaftale eller på regningen (typisk 0–10 øre ekskl. moms).")
            : L.T($"Everything you pay per kWh on top of the spot price – the supplier's margin, grid fees and taxes – in {country.Unit.Symbol} excl. VAT. Leave it at 0 to see the spot price.",
                  $"Alt, du betaler pr. kWh oven i spotprisen – elselskabets tillæg, netgebyrer og afgifter – i {country.Unit.Symbol} ekskl. moms. Lad det stå på 0 for at se spotprisen.");

        var selected = (_area.SelectedItem as AreaItem)?.Area.Code;
        _area.BeginUpdate();
        _area.Items.Clear();
        _area.Items.AddRange(country.Areas.Select(a => (object)new AreaItem(a)).ToArray());
        _area.EndUpdate();
        _area.SelectedItem = _area.Items.Cast<AreaItem>().FirstOrDefault(i => i.Area.Code == (selected ?? _loadedArea))
                             ?? _area.Items[0];
    }

    private string _lastDetectedOwner = "";
    private string? _loadedArea;

    public override void LoadFrom(AppSettings s)
    {
        var country = s.PriceCountry;
        _loadedArea = s.PriceArea;
        _country.SelectedItem = (object?)country ?? OtherCountry;
        _vatCountry = country;
        ApplyCountry();
        _postal.Text = s.PostalCode ?? "";
        (s.PriceArea == "DK2" ? _dk2 : _dk1).Checked = true;
        _spotOnly.Checked = !s.PriceShowTotal;
        _totalPanel.Enabled = s.PriceShowTotal;
        _company.Text = s.NetTariffOwner;
        _loadedOwner = s.NetTariffOwner;
        _loadedCodes = s.NetTariffCodes;
        _supplierName.Text = s.SupplierName ?? "";
        _supplierAddOn.Value = (decimal)Math.Clamp(s.SupplierAddOnOre, -100, 500);
        _vatPercent.Value = (decimal)Math.Clamp(s.EffectiveVatPercent, 0, 40);
        _vat.Checked = s.PriceInclVat;
        _showPrice.Checked = s.ShowPrice;
    }

    public override async Task OnFirstShownAsync()
    {
        if (SelectedCountry?.Code == "DK") await EnsureCompaniesAsync();
    }

    /// <summary>Fetches the Danish grid companies the first time Denmark is shown.</summary>
    private async Task EnsureCompaniesAsync()
    {
        if (_companiesRequested) return;
        _companiesRequested = true;
        _companyStatus.Text = L.T("Fetching grid companies from Energinet…", "Henter netselskaber fra Energinet…");
        _companyStatus.ForeColor = Ui.Muted;
        try
        {
            _companyCache ??= await TariffCatalog.GetGridCompaniesAsync(Http);
            var current = _company.Text;
            _company.BeginUpdate();
            _company.Items.Clear();
            _company.Items.AddRange(_companyCache.Cast<object>().ToArray());
            _company.EndUpdate();
            _company.Text = current;
            _companyStatus.Text = L.T($"{_companyCache.Count} grid companies – type to search", $"{_companyCache.Count} netselskaber – skriv for at søge");

            if (!string.IsNullOrWhiteSpace(current)) await DetectAsync(current);
        }
        catch (Exception ex)
        {
            _companiesRequested = false;
            _companyStatus.Text = L.T("Could not fetch grid companies – check the internet connection", "Kunne ikke hente netselskaber – tjek internetforbindelsen");
            _companyStatus.ForeColor = Ui.Error;
            Logger.Error($"Grid companies could not be fetched: {ex.Message}");
        }
    }

    private async Task DetectAsync(string owner)
    {
        owner = owner.Trim();
        if (owner.Length == 0) return;
        var version = ++_detectVersion;
        _lastDetectedOwner = owner;
        _tariff.Items.Clear();
        _tariffPreview.Text = "";
        _companyStatus.Text = L.T("Finding the tariff…", "Finder tarif…");
        _companyStatus.ForeColor = Ui.Muted;
        try
        {
            var detection = await TariffCatalog.DetectAsync(Http, owner);
            if (version != _detectVersion) return;
            _detection = detection;

            if (detection.Candidates.Count == 0)
            {
                _companyStatus.Text = L.T("No C tariff found for this grid company", "Ingen C-tarif fundet for dette netselskab");
                _companyStatus.ForeColor = Ui.Warn;
                return;
            }

            _tariff.Items.AddRange(detection.Candidates.Cast<object>().ToArray());
            var preferred = detection.Candidates.FirstOrDefault(c => _loadedCodes.Contains(c.Code) && owner == _loadedOwner);
            _tariff.SelectedItem = preferred ?? detection.Candidates[0];
            _companyStatus.Text = detection.Candidates.Count == 1
                ? L.T("✓ Tariff found", "✓ Tarif fundet")
                : L.T($"✓ {detection.Candidates.Count} tariffs found – the most common one is selected", $"✓ {detection.Candidates.Count} tariffer fundet – den mest almindelige er valgt");
            _companyStatus.ForeColor = Ui.Ok;
        }
        catch (Exception ex)
        {
            if (version != _detectVersion) return;
            _companyStatus.Text = L.T("Could not fetch tariffs – try again", "Kunne ikke hente tariffer – prøv igen");
            _companyStatus.ForeColor = Ui.Error;
            Logger.Error($"Tariff detection failed for {owner}: {ex.Message}");
        }
    }

    private void UpdatePreview()
    {
        if (_tariff.SelectedItem is not TariffCandidate c || _detection == null)
        {
            _tariffPreview.Text = "";
            return;
        }
        double Sum(Func<TariffCandidate, double> pick) => pick(c) + _detection.Discounts.Sum(pick);
        string F(double v) => v.ToString("0.0", Fmt);
        var discount = _detection.Discounts.Count > 0 ? L.T(" (incl. discount)", " (inkl. rabat)") : "";
        _tariffPreview.Text = c.TimeDifferentiated
            ? L.T($"Night {F(Sum(t => t.NightOre))} · day {F(Sum(t => t.DayOre))} · 17–21 {F(Sum(t => t.PeakOre))} øre/kWh excl. VAT{discount}",
                  $"Nat {F(Sum(t => t.NightOre))} · dag {F(Sum(t => t.DayOre))} · kl. 17–21 {F(Sum(t => t.PeakOre))} øre/kWh ekskl. moms{discount}")
            : L.T($"{F(Sum(t => t.DayOre))} øre/kWh excl. VAT{discount}", $"{F(Sum(t => t.DayOre))} øre/kWh ekskl. moms{discount}");
    }

    public override string? SaveTo(AppSettings s)
    {
        var country = SelectedCountry;
        s.Country = country?.Code ?? Countries.Other;
        if (country == null) return null; // the price is hidden; keep the rest for when a country is chosen again

        s.ShowPrice = _showPrice.Checked;
        s.PriceShowTotal = !_spotOnly.Checked;
        s.PriceInclVat = _vat.Checked;
        s.VatPercent = (double)_vatPercent.Value == country.VatPercent ? null : (double)_vatPercent.Value;
        s.SupplierName = string.IsNullOrWhiteSpace(_supplierName.Text) ? null : _supplierName.Text.Trim();
        s.SupplierAddOnOre = (double)_supplierAddOn.Value;

        if (country.Code != "DK")
        {
            s.PriceArea = (_area.SelectedItem as AreaItem)?.Area.Code ?? country.DefaultArea.Code;
            return null;
        }

        var postal = _postal.Text.Trim();
        if (postal.Length > 0 && TariffCatalog.AreaFromPostalCode(postal) == null)
            return L.T("The postal code must be four digits.", "Postnummeret skal være fire cifre.");

        s.PostalCode = postal.Length > 0 ? postal : null;
        s.PriceArea = _dk2.Checked ? "DK2" : "DK1";

        if (_spotOnly.Checked) return null;

        var owner = _company.Text.Trim();
        if (_tariff.SelectedItem is TariffCandidate tariff && _detection != null && owner == _lastDetectedOwner)
        {
            s.NetTariffOwner = owner;
            s.NetTariffCodes = new[] { tariff.Code }.Concat(_detection.Discounts.Select(d => d.Code)).ToArray();
            return null;
        }
        if (owner.Length > 0 && owner == _loadedOwner && _loadedCodes.Length > 0)
        {
            s.NetTariffOwner = owner;       // unchanged (e.g. offline)
            s.NetTariffCodes = _loadedCodes;
            return null;
        }
        return L.T("Choose your grid company so the grid tariff can be found – or choose \"Show the spot price only\".",
                   "Vælg dit netselskab, så nettariffen kan findes – eller vælg \"Vis kun spotpris\".");
    }
}

// ---------------------------------------------------------------------------

internal sealed class NotificationsPage : SettingsPage
{
    private readonly CheckBox _cheap = Ui.Check(L.T("When the cheapest 3 hours are about to start", "Når de billigste 3 timer snart starter"));
    private readonly CheckBox _expensive = Ui.Check(L.T("When power is about to get expensive", "Når strømmen snart bliver dyr"));
    private readonly NumericUpDown _lead = new() { Width = 70, Minimum = 5, Maximum = 240, Increment = 5 };
    private readonly CheckBox _quiet = Ui.Check(L.T("No price messages at night (23–07)", "Ingen prisbeskeder om natten (kl. 23–07)"));
    private readonly CheckBox _ipChange = Ui.Check(L.T("When my external IP address changes", "Når min eksterne IP-adresse skifter"));
    private readonly CheckBox _cfError = Ui.Check(L.T("When the Cloudflare update fails", "Når Cloudflare-opdateringen fejler"));
    private readonly CheckBox _tunnelDown = Ui.Check(L.T("When a Cloudflare Tunnel goes down", "Når en Cloudflare Tunnel går ned"));
    private readonly CheckBox _serviceDown = Ui.Check(L.T("When a service behind a tunnel stops responding", "Når en tjeneste bag en tunnel holder op med at svare"));

    public override string Title => L.T("Notifications", "Notifikationer");
    public override string Glyph => "\uEA8F";

    public NotificationsPage()
    {
        Add(Ui.Heading(L.T("Notifications", "Notifikationer")),
            Ui.Help(L.T("Messages are shown as Windows notifications with buttons to open the widget.", "Beskederne vises som Windows-notifikationer med knapper til at åbne widgetten.")),
            Ui.Section(L.T("Price alerts", "Prisalarmer")),
            _cheap,
            _expensive,
            Ui.Row(L.T("Notify", "Send besked"), _lead, Ui.Inline(L.T("minutes before", "minutter før"))),
            _quiet,
            Ui.Section(L.T("Network", "Netværk")),
            _ipChange,
            _cfError,
            _tunnelDown,
            _serviceDown);
    }

    public override void LoadFrom(AppSettings s)
    {
        _cheap.Checked = s.AlertCheap;
        _expensive.Checked = s.AlertExpensive;
        _lead.Value = Math.Clamp(s.AlertLeadMinutes, 5, 240);
        _quiet.Checked = s.AlertQuietNight;
        _ipChange.Checked = s.NotifyIpChange;
        _cfError.Checked = s.NotifyCloudflareError;
        _tunnelDown.Checked = s.NotifyTunnelDown;
        _serviceDown.Checked = s.NotifyServiceDown;
    }

    public override string? SaveTo(AppSettings s)
    {
        s.AlertCheap = _cheap.Checked;
        s.AlertExpensive = _expensive.Checked;
        s.AlertLeadMinutes = (int)_lead.Value;
        s.AlertQuietNight = _quiet.Checked;
        s.NotifyIpChange = _ipChange.Checked;
        s.NotifyCloudflareError = _cfError.Checked;
        s.NotifyTunnelDown = _tunnelDown.Checked;
        s.NotifyServiceDown = _serviceDown.Checked;
        return null;
    }
}

// ---------------------------------------------------------------------------

internal sealed class GeneralPage : SettingsPage
{
    private readonly ComboBox _language = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _theme = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TrackBar _opacity = new() { Minimum = 40, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10, Width = 200, AutoSize = false, Height = 30 };
    private readonly Label _opacityValue = Ui.Inline("");
    private readonly CheckBox _topMost = Ui.Check(L.T("Keep the widget and pinned windows on top of other windows", "Hold widgetten og fastgjorte vinduer øverst over andre vinduer"));
    private readonly CheckBox _startup = Ui.Check(L.T("Start automatically when I sign in to Windows", "Start automatisk når jeg logger på Windows"));
    private readonly CheckBox _autoUpdate = Ui.Check(L.T("Check for new versions automatically", "Søg automatisk efter nye versioner"));
    private readonly CheckBox _autoInstall = Ui.Check(L.T("Install them by themselves when the PC is not in use", "Installér dem selv, når pc'en ikke er i brug"));

    public override string Title => L.T("General", "Generelt");
    public override string Glyph => "";

    public GeneralPage()
    {
        _language.Items.AddRange(new object[] { "English", "Dansk" });
        _theme.Items.AddRange(new object[] { L.T("Follow Windows", "Følg Windows"), L.T("Light", "Lyst"), L.T("Dark", "Mørkt") });
        _opacity.ValueChanged += (_, _) => _opacityValue.Text = $"{_opacity.Value} %";
        _autoUpdate.CheckedChanged += (_, _) => _autoInstall.Enabled = _autoUpdate.Checked;

        Add(Ui.Heading(L.T("General", "Generelt")),
            Ui.Help(L.T("Language, look and how LabWidge starts and keeps itself up to date.", "Sprog, udseende og hvordan LabWidge starter og holder sig opdateret.")),
            Ui.Section(L.T("Language", "Sprog")),
            Ui.Row(L.T("Language", "Sprog"), _language),
            Ui.Help(L.T("LabWidge restarts to switch language.", "LabWidge genstarter for at skifte sprog.")),
            Ui.Section(L.T("Appearance", "Udseende")),
            Ui.Row(L.T("Theme", "Tema"), _theme),
            Ui.Row(L.T("Opacity", "Uigennemsigtighed"), _opacity, _opacityValue),
            _topMost,
            Ui.Section(L.T("Startup and updates", "Opstart og opdatering")),
            _startup,
            _autoUpdate,
            _autoInstall,
            Ui.Help(L.T($"You are running version {UpdateService.Current}. New versions come from GitHub. With automatic installation they are " +
                        "downloaded in the background and installed when the mouse and keyboard have not been touched for 10 minutes – otherwise you are asked first.",
                        $"Du kører version {UpdateService.Current}. Nye versioner hentes fra GitHub. Med automatisk installation hentes " +
                        "de i baggrunden og installeres, når mus og tastatur ikke har været rørt i 10 minutter – ellers bliver du spurgt først.")));
    }

    public override void LoadFrom(AppSettings s)
    {
        _language.SelectedIndex = L.Normalize(s.Language) == L.Danish ? 1 : 0;
        _theme.SelectedIndex = (int)s.Theme;
        _opacity.Value = Math.Clamp(s.WidgetOpacity, 40, 100);
        _opacityValue.Text = $"{_opacity.Value} %";
        _topMost.Checked = s.WidgetTopMost;
        _startup.Checked = s.StartWithWindows;
        _autoUpdate.Checked = s.AutoCheckUpdates;
        _autoInstall.Checked = s.AutoInstallUpdates;
        _autoInstall.Enabled = s.AutoCheckUpdates;
    }

    public override string? SaveTo(AppSettings s)
    {
        s.Language = _language.SelectedIndex == 1 ? L.Danish : L.English;
        s.Theme = (WidgetTheme)Math.Max(0, _theme.SelectedIndex);
        s.WidgetOpacity = _opacity.Value;
        s.WidgetTopMost = _topMost.Checked;
        s.StartWithWindows = _startup.Checked;
        s.AutoCheckUpdates = _autoUpdate.Checked;
        s.AutoInstallUpdates = _autoInstall.Checked;
        return null;
    }
}

// ---------------------------------------------------------------------------

internal sealed class WidgetPage : SettingsPage
{
    private readonly CheckBox _compact = Ui.Check(L.T("Compact view (a summary per plugin)", "Kompakt visning (en oversigt pr. plugin)"));
    private readonly Button _resetOrder = new() { Text = L.T("Reset the order", "Nulstil rækkefølgen"), AutoSize = true };
    private readonly Button _resetPins = new() { Text = L.T("Unpin all sections", "Frigør alle sektioner"), AutoSize = true };
    private readonly NumericUpDown _width = new() { Minimum = 300, Maximum = 640, Increment = 20, Width = 80 };
    private readonly NumericUpDown _height = new() { Minimum = 180, Maximum = 2160, Increment = 20, Width = 80 };
    private readonly CheckBox _autoWidth = Ui.Check(L.T("Automatic width", "Automatisk bredde"));
    private readonly CheckBox _autoHeight = Ui.Check(L.T("Automatic height", "Automatisk højde"));
    private bool _orderReset, _pinsReset;

    public override string Title => "Widget";
    public override string Glyph => "";

    public WidgetPage()
    {
        _autoWidth.Width = _autoHeight.Width = 200;
        _autoWidth.CheckedChanged += (_, _) => _width.Enabled = !_autoWidth.Checked;
        _autoHeight.CheckedChanged += (_, _) => _height.Enabled = !_autoHeight.Checked;
        _resetPins.Click += (_, _) => { _pinsReset = true; _resetPins.Text = L.T("Sections are unpinned when you save", "Sektionerne frigøres ved Gem"); };
        _resetOrder.Click += (_, _) =>
        {
            _orderReset = true;
            _resetOrder.Enabled = false;
            _resetOrder.Text = L.T("Default order when you save", "Standardrækkefølge ved Gem");
        };

        Add(Ui.Heading("Widget"),
            Ui.Help(L.T("Click the bolt by the clock to show or hide the widget. Click a header to collapse a section, drag its handle to move it, " +
                        "and drag it out of the widget to open it in a window.",
                        "Klik på lynet ved uret for at vise eller skjule widgetten. Klik på en overskrift for at folde en sektion sammen, træk i grebet for at flytte den, " +
                        "og træk den ud af widgetten for at åbne den i et vindue.")),
            Ui.Section(L.T("Sections", "Sektioner")),
            Ui.Help(L.T("Choose active sections on the Plugins page. Configure each active plugin on its own page.",
                        "Vælg aktive sektioner på Plugins-siden. Indstil hvert aktivt plugin på dets egen side.")),
            Ui.Row(L.T("Order and pins", "Rækkefølge og pinning"), _resetOrder, _resetPins),
            Ui.Section(L.T("Size", "Størrelse")),
            Ui.Help(L.T("You can also drag the widget's edges or corners. The size is remembered across display scaling.",
                        "Du kan også trække i widgettens kanter eller hjørner. Størrelsen huskes på tværs af skærmskalering.")),
            Ui.Row(L.T("Width", "Bredde"), _width, _autoWidth),
            Ui.Row(L.T("Height", "Højde"), _height, _autoHeight),
            _compact);
    }

    public override void LoadFrom(AppSettings s)
    {
        _compact.Checked = s.CompactMode;
        _width.Value = Math.Clamp(s.WidgetWidth ?? 344, 300, 640);
        _height.Value = Math.Clamp(s.WidgetHeight ?? 600, 180, 2160);
        _autoWidth.Checked = s.WidgetWidth == null;
        _autoHeight.Checked = s.WidgetHeight == null;
        _width.Enabled = !_autoWidth.Checked;
        _height.Enabled = !_autoHeight.Checked;
    }

    public override string? SaveTo(AppSettings s)
    {
        s.CompactMode = _compact.Checked;
        if (_orderReset) s.SectionOrder = null;
        s.WidgetWidth = _autoWidth.Checked ? null : (int)_width.Value;
        s.WidgetHeight = _autoHeight.Checked ? null : (int)_height.Value;
        if (_pinsReset) { s.SectionPins.Clear(); s.SectionLastPins.Clear(); s.SectionPinSummaries = Array.Empty<string>(); }
        return null;
    }
}

// ---------------------------------------------------------------------------

internal sealed class ShortcutsPage : SettingsPage
{
    private readonly CheckBox _show = Ui.Check(L.T("Show the shortcuts in the widget", "Vis genvejene i widgetten"));
    private readonly ComboBox _placement = new() { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly DataGridView _grid = new()
    {
        Width = Ui.ContentWidth,
        Height = 230,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
        BorderStyle = BorderStyle.FixedSingle,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        Margin = new Padding(0, 4, 0, 4)
    };
    private readonly Button _addWeb = new() { Text = L.T("Add website", "Tilføj hjemmeside"), AutoSize = true };
    private readonly Button _addProgram = new() { Text = L.T("Add program …", "Tilføj program …"), AutoSize = true };
    private readonly Button _examples = new() { Text = L.T("Add suggestions", "Tilføj forslag"), AutoSize = true };
    private readonly Button _icon = new() { Text = L.T("Choose icon …", "Vælg ikon …"), AutoSize = true };
    private readonly Button _defaultIcon = new() { Text = L.T("Automatic icon", "Automatisk ikon"), AutoSize = true };
    private readonly Button _up = new() { Text = L.T("Move up", "Flyt op"), AutoSize = true };
    private readonly Button _down = new() { Text = L.T("Move down", "Flyt ned"), AutoSize = true };
    private readonly Button _remove = new() { Text = L.T("Remove", "Fjern"), AutoSize = true };
    private readonly Button _reload = new() { Text = L.T("Fetch the icons again", "Hent ikonerne igen"), AutoSize = true };
    private readonly Dictionary<string, Bitmap> _letters = new();

    public override string Title => L.T("Shortcuts", "Genveje");
    public override string Glyph => "";

    public ShortcutsPage()
    {
        _grid.RowTemplate.Height = 30;
        _grid.Columns.Add(new DataGridViewImageColumn { HeaderText = "", FillWeight = 9, ReadOnly = true, ImageLayout = DataGridViewImageCellLayout.Zoom });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Name", "Navn"), FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Website or program", "Hjemmeside eller program"), FillWeight = 61 });
        _grid.Columns[0].DefaultCellStyle.Padding = new Padding(4);
        _grid.CellEndEdit += (_, e) => { if (e.RowIndex >= 0) UpdateRow(_grid.Rows[e.RowIndex]); };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) PickIcon(); };

        _addWeb.Click += (_, _) =>
        {
            var row = AddRow(new LaunchItem { Target = "https://" });
            _grid.CurrentCell = row.Cells[2];
            _grid.BeginEdit(false);
            if (_grid.EditingControl is TextBox box) box.SelectionStart = box.TextLength;
        };
        _addProgram.Click += (_, _) => AddProgram();
        _examples.Click += (_, _) =>
        {
            var existing = Items().Select(i => i.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in LaunchItem.Examples().Where(i => !existing.Contains(i.DisplayName))) AddRow(item);
        };
        _icon.Click += (_, _) => PickIcon();
        _defaultIcon.Click += (_, _) =>
        {
            if (_grid.CurrentRow is not { } row || row.Tag is not LaunchItem item) return;
            item.IconPath = null;
            UpdateRow(row);
        };
        _up.Click += (_, _) => MoveRow(-1);
        _down.Click += (_, _) => MoveRow(1);
        _remove.Click += (_, _) => { if (_grid.CurrentRow is { } row) _grid.Rows.Remove(row); };
        _reload.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in _grid.Rows) row.Cells[0].Value = null; // the old bitmaps are disposed
            LaunchIcons.Reload();
            RefreshIcons();
        };
        LaunchIcons.Updated += OnIconsUpdated;
        Disposed += (_, _) =>
        {
            LaunchIcons.Updated -= OnIconsUpdated;
            foreach (var letter in _letters.Values) letter.Dispose();
        };

        _placement.Items.AddRange(new object[]
        {
            L.T("Rail on the left", "Rail i venstre side"), L.T("Rail on the right", "Rail i højre side"), L.T("A section in the widget", "En sektion i widgetten")
        });
        _show.CheckedChanged += (_, _) => _placement.Enabled = _show.Checked;

        Add(Ui.Heading(L.T("Shortcuts", "Genveje")),
            Ui.Help(L.T("Large icons in the widget that open your favourite websites and programs with one click – e.g. YouTube, Instagram, ChatGPT or Discord.",
                        "Store ikoner i widgetten, der åbner dine foretrukne hjemmesider og programmer med ét klik – fx YouTube, Instagram, ChatGPT eller Discord.")),
            _show,
            Ui.Row(L.T("Placement", "Placering"), _placement),
            Ui.Help(L.T("The rail is tucked in behind the widget with small icons and slides out when you point at it. Drag an icon to change the order, " +
                        "and drag the grip at the top to move the rail up or down, to the other side, or into the widget as a section – drag the section's header " +
                        "back to an edge. Right-click the rail for the same choices.",
                        "Rail'en ligger gemt bag widgetten med små ikoner og glider ud, når du peger på den. Træk et ikon for at ændre rækkefølgen, " +
                        "og træk i grebet øverst for at flytte rail'en op eller ned, til den anden side eller ind i widgetten som en sektion – træk sektionens overskrift " +
                        "tilbage til en kant. Højreklik på rail'en for de samme valg.")),
            Ui.Section(L.T("Your shortcuts", "Dine genveje")),
            Ui.Help(L.T("Type a web address (youtube.com), or choose a program or a shortcut from the Start menu. The order is the same as in the rail. " +
                        "Double-click the icon to choose your own.",
                        "Skriv en webadresse (youtube.com), eller vælg et program eller en genvej fra Start-menuen. Rækkefølgen er den samme som i rail'en. " +
                        "Dobbeltklik på ikonet for at vælge dit eget.")),
            _grid,
            Buttons(_addWeb, _addProgram, _examples),
            Buttons(_up, _down, _remove),
            Ui.Section(L.T("Icons", "Ikoner")),
            Buttons(_icon, _defaultIcon),
            Ui.Help(L.T("Programs use their Windows icon. A website's icon is fetched from the website itself once and kept on the PC. " +
                        "If a site has no usable icon, its first letter is shown.",
                        "Programmer bruger deres Windows-ikon. En hjemmesides ikon hentes én gang fra hjemmesiden selv og gemmes på pc'en. " +
                        "Har en side intet brugbart ikon, vises dens forbogstav.")),
            Buttons(_reload));
    }

    /// <summary>A row of buttons under the list, starting at its left edge.</summary>
    private static FlowLayoutPanel Buttons(params Button[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 4) };
        foreach (var button in buttons)
        {
            button.Margin = new Padding(0, 0, 8, 0);
            row.Controls.Add(button);
        }
        return row;
    }

    private IEnumerable<LaunchItem> Items() => _grid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<LaunchItem>();

    private DataGridViewRow AddRow(LaunchItem item)
    {
        var index = _grid.Rows.Add(null, item.Name, item.Target);
        var row = _grid.Rows[index];
        row.Tag = new LaunchItem { Name = item.Name, Target = item.Target, Arguments = item.Arguments, IconPath = item.IconPath };
        UpdateRow(row);
        return row;
    }

    /// <summary>Copies the cells into the row's item and shows its icon.</summary>
    private void UpdateRow(DataGridViewRow row)
    {
        if (row.Tag is not LaunchItem item) return;
        item.Name = (row.Cells[1].Value as string ?? "").Trim();
        item.Target = (row.Cells[2].Value as string ?? "").Trim();
        row.Cells[0].Value = IconFor(item);
        row.Cells[0].ToolTipText = item.IconPath ?? L.T("Double-click to choose an icon", "Dobbeltklik for at vælge et ikon");
    }

    private Image? IconFor(LaunchItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Target) || item.Target == "https://") return null;
        if (LaunchIcons.Get(item) is { } icon) return icon;
        var name = item.DisplayName;
        if (!_letters.TryGetValue(name, out var letter))
        {
            letter = new Bitmap(48, 48);
            using var g = Graphics.FromImage(letter);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var font = new Font("Segoe UI Semibold", 22, FontStyle.Regular, GraphicsUnit.Pixel);
            LaunchIcons.DrawLetter(g, name, new RectangleF(0, 0, 48, 48), font);
            _letters[name] = letter;
        }
        return letter;
    }

    private void OnIconsUpdated()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(RefreshIcons));
    }

    private void RefreshIcons()
    {
        foreach (DataGridViewRow row in _grid.Rows)
            if (row.Tag is LaunchItem item) row.Cells[0].Value = IconFor(item);
    }

    private void AddProgram()
    {
        using var dialog = new OpenFileDialog
        {
            Title = L.T("Choose a program or a shortcut", "Vælg et program eller en genvej"),
            Filter = L.T("Programs and shortcuts", "Programmer og genveje") + "|*.exe;*.lnk;*.url;*.bat;*.cmd;*.appref-ms|"
                     + L.T("All files", "Alle filer") + "|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            DereferenceLinks = false // keep the shortcut, so its icon, arguments and folder come along
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var row = AddRow(new LaunchItem { Name = Path.GetFileNameWithoutExtension(dialog.FileName), Target = dialog.FileName });
        _grid.CurrentCell = row.Cells[1];
    }

    private void PickIcon()
    {
        if (_grid.CurrentRow is not { } row || row.Tag is not LaunchItem item) return;
        using var dialog = new OpenFileDialog
        {
            Title = L.T("Choose an icon", "Vælg et ikon"),
            Filter = L.T("Images and programs", "Billeder og programmer") + "|*.png;*.ico;*.jpg;*.jpeg;*.gif;*.bmp;*.exe;*.lnk|"
                     + L.T("All files", "Alle filer") + "|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        item.IconPath = dialog.FileName;
        UpdateRow(row);
    }

    private void MoveRow(int delta)
    {
        if (_grid.CurrentRow is not { } row) return;
        var to = row.Index + delta;
        if (to < 0 || to >= _grid.Rows.Count) return;
        _grid.Rows.RemoveAt(row.Index);
        _grid.Rows.Insert(to, row);
        _grid.CurrentCell = row.Cells[1];
    }

    public override void LoadFrom(AppSettings s)
    {
        _show.Checked = s.ShowLaunchRail;
        _placement.SelectedIndex = (int)s.LaunchRailPlacement;
        _placement.Enabled = s.ShowLaunchRail;
        _grid.Rows.Clear();
        foreach (var item in s.LaunchItems) AddRow(item);
    }

    public override string? SaveTo(AppSettings s)
    {
        _grid.EndEdit();
        foreach (DataGridViewRow row in _grid.Rows) UpdateRow(row);
        s.ShowLaunchRail = _show.Checked;
        var placement = (RailPlacement)Math.Max(0, _placement.SelectedIndex);
        // A section chosen here is placed after the last section, unless the user has already put it somewhere
        if (placement == RailPlacement.Section && s.LaunchRailPlacement != RailPlacement.Section && s.SectionOrder != null)
            s.SectionOrder = s.SectionOrder.Where(k => k != DashboardForm.ShortcutsKey).Append(DashboardForm.ShortcutsKey).ToArray();
        s.LaunchRailPlacement = placement;
        s.LaunchItems = Items().Where(i => i.Target.Length > 0 && i.Target != "https://").ToList();
        return null;
    }
}

// ---------------------------------------------------------------------------

internal sealed class WindowsPage : SettingsPage
{
    private readonly CheckBox _restore = Ui.Check(L.T("Open pinned windows again when LabWidge starts", "Åbn fastgjorte vinduer igen, når LabWidge starter"));
    private readonly FlowLayoutPanel _list = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
    private readonly HashSet<string> _forget = new();
    private Dictionary<string, SectionWindowState> _states = new();

    public override string Title => L.T("Windows", "Vinduer");
    public override string Glyph => "";

    public WindowsPage()
    {
        var forgetAll = new Button { Text = L.T("Forget all windows", "Glem alle vinduer"), AutoSize = true };
        forgetAll.Click += (_, _) =>
        {
            foreach (var key in SectionWindows.Keys) _forget.Add(key);
            Fill();
        };

        Add(Ui.Heading(L.T("Windows", "Vinduer")),
            Ui.Help(L.T("Every section can open in its own window with more detail: drag the section out of the widget, click the window button in its header, " +
                        "or choose \"Open in a window\" when you right-click it. A window closes when you click elsewhere – unless you pin it.",
                        "Hver sektion kan åbne i sit eget vindue med flere detaljer: træk sektionen ud af widgetten, klik på vindue-knappen i overskriften, " +
                        "eller vælg \"Åbn i et vindue\", når du højreklikker. Et vindue lukker, når du klikker andre steder – medmindre du fastgør det.")),
            Ui.Section(L.T("Behaviour", "Adfærd")),
            _restore,
            Ui.Section(L.T("Your windows", "Dine vinduer")),
            Ui.Help(L.T("Pinned windows remember their place and size. Forget a window to unpin it and let it open next to the widget again.",
                        "Fastgjorte vinduer husker deres plads og størrelse. Glem et vindue for at frigøre det og lade det åbne ved siden af widgetten igen.")),
            _list,
            forgetAll);
    }

    private static string WindowName(string key) => key switch
    {
        "price" => L.T("Electricity price", "Elpris"),
        "ha" => "Home Assistant",
        "cloudflare" => "Cloudflare",
        "proxmox" => "Proxmox",
        "system" => "System",
        "network" => L.T("Network", "Netværk"),
        "audio" => L.T("Audio", "Lyd"),
        _ => key
    };

    private void Fill()
    {
        _list.SuspendLayout();
        _list.Controls.Clear();
        foreach (var key in SectionWindows.Keys)
        {
            var state = _forget.Contains(key) ? null : _states.GetValueOrDefault(key);
            var status = state == null ? L.T("opens next to the widget", "åbner ved siden af widgetten")
                : state.Pinned ? L.T("pinned", "fastgjort") + (state.Width != null ? L.T(" · own size", " · egen størrelse") : "")
                : state.Width != null ? L.T("own size", "egen størrelse") : L.T("opens next to the widget", "åbner ved siden af widgetten");
            var label = Ui.Inline(status, state?.Pinned == true ? Ui.Accent : Ui.Muted);
            label.MinimumSize = new Size(190, 0);
            var forget = new Button { Text = L.T("Forget", "Glem"), AutoSize = true, Enabled = state != null };
            forget.Click += (_, _) => { _forget.Add(key); Fill(); };
            _list.Controls.Add(Ui.Row(WindowName(key), label, forget));
        }
        SettingsTheme.Apply(_list, Ui.Palette);
        _list.ResumeLayout();
    }

    public override void LoadFrom(AppSettings s)
    {
        _restore.Checked = s.RestorePinnedWindows;
        _states = s.SectionWindows.ToDictionary(kv => kv.Key, kv => kv.Value);
        Fill();
    }

    public override string? SaveTo(AppSettings s)
    {
        s.RestorePinnedWindows = _restore.Checked;
        s.ForgetWindows = _forget.ToArray();
        return null;
    }
}

// ---------------------------------------------------------------------------

internal sealed class AboutPage : SettingsPage
{
    public override string Title => L.T("About", "Om");
    public override string Glyph => "";

    public AboutPage()
    {
        var logo = new PictureBox { Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 0, 6) };
        // The shared app icon – not disposed here
        if (AppIconProvider.GetIcon() is { } icon) logo.Image = new Icon(icon, 64, 64).ToBitmap();

        Add(logo,
            Ui.Heading("LabWidge"),
            Ui.Help(L.T($"Version {UpdateService.Current}  ·  Electricity price, PC and home lab – at a glance, right by the clock.",
                        $"Version {UpdateService.Current}  ·  Elpris, pc og hjemmelab – med et blik, lige ved uret.")),
            Ui.Section(L.T("Links", "Links")),
            Link(L.T("The project on GitHub – features, help and source code", "Projektet på GitHub – funktioner, hjælp og kildekode"), "https://github.com/Karalumpas/LabWidge"),
            Link(L.T("What's new in each version", "Nyheder i hver version"), "https://github.com/Karalumpas/LabWidge/blob/main/CHANGELOG.md"),
            Link(L.T("Report a problem or suggest an idea", "Meld en fejl eller foreslå en idé"), "https://github.com/Karalumpas/LabWidge/issues"),
            Link(L.T("Privacy – what LabWidge contacts and why", "Privatliv – hvad LabWidge kontakter og hvorfor"), "https://github.com/Karalumpas/LabWidge/blob/main/PRIVACY.md"),
            Ui.Section(L.T("Troubleshooting", "Fejlfinding")),
            Folder(L.T("Open the folder with settings and the log", "Åbn mappen med indstillinger og log"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabWidge")),
            Ui.Help(L.T("Free and open source under the MIT license. No telemetry and no account.",
                        "Gratis og open source under MIT-licensen. Ingen telemetri og ingen konto.")));
    }

    private static LinkLabel Link(string text, string url)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return link;
    }

    private static LinkLabel Folder(string text, string path)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Error($"Could not open {path}: {ex.Message}"); }
        };
        return link;
    }

    public override void LoadFrom(AppSettings s) { }
    public override string? SaveTo(AppSettings s) => null;
}

// ---------------------------------------------------------------------------

internal sealed class CloudflarePage : SettingsPage
{
    private readonly CheckBox _enabled = Ui.Check(L.T("I use Cloudflare for DNS and/or tunnels", "Jeg bruger Cloudflare til DNS og/eller tunnels"));
    private readonly NumericUpDown _interval = new() { Width = 70, Minimum = 1, Maximum = 60 };
    private readonly CheckBox _showInWidget = Ui.Check(L.T("Show Cloudflare – tunnels and DNS – in the widget", "Vis Cloudflare – tunnels og DNS – i widgetten"));
    private readonly CheckBox _serviceChecks = Ui.Check(L.T("Check that the addresses behind my tunnels respond (every 5 minutes)", "Tjek at adresserne bag mine tunnels svarer (hvert 5. minut)"));
    private readonly FlowLayoutPanel _details;
    private readonly TextBox _zoneId = new() { Width = 290 };
    private readonly TextBox _accountId = new() { Width = 290 };
    private readonly TextBox _token = new() { Width = 290, UseSystemPasswordChar = true };
    private readonly Label _tokenStatus = Ui.Inline("");
    private readonly Button _deleteToken = new() { Text = L.T("Delete", "Slet"), AutoSize = true };
    private readonly CheckBox _autoUpdate = Ui.Check(L.T("Update automatically when my IP changes", "Opdatér automatisk når min IP skifter"));
    private readonly CheckBox _updateAll = Ui.Check(L.T("Update all A records in the zone", "Opdatér alle A-records i zonen"));
    private readonly TextBox _hosts = new() { Width = 290, Height = 70, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _test = new() { Text = L.T("Test connection", "Test forbindelse"), AutoSize = true };
    private readonly Label _testResult = Ui.Inline("");

    public override string Title => "Cloudflare";
    public override string Glyph => "\uE753";

    private bool _managedPlugin;

    public override void UsePluginActivation()
    {
        _managedPlugin = true;
        _enabled.Visible = false;
        _enabled.Checked = true;
        _details.Enabled = true;
    }

    public CloudflarePage()
    {
        _details = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(20, 4, 0, 0) };
        _details.Controls.AddRange(new Control[]
        {
            Ui.Row("Zone ID", _zoneId),
            Ui.Row(L.T("Account ID (for tunnels)", "Account ID (til tunnels)"), _accountId),
            Ui.Row(L.T("API token", "API-token"), _token),
            Ui.Row("", _tokenStatus, _deleteToken),
            Ui.Help(L.T("Create a token under My Profile → API Tokens with the permission Zone → DNS → Edit. " +
                        "For the widget to show your tunnels too, also give it Account → Cloudflare Tunnel → Read and fill in the Account ID " +
                        "(found under Overview in the Cloudflare dashboard). The token is stored safely in Windows Credential Manager. " +
                        "Leave the field empty to keep the saved token.",
                        "Opret et token under My Profile → API Tokens med rettigheden Zone → DNS → Edit. " +
                        "Skal widgetten også vise dine tunnels, så giv det desuden Account → Cloudflare Tunnel → Read og udfyld Account ID " +
                        "(står under Overview i Cloudflare-dashboardet). Tokenet gemmes sikkert i Windows Credential Manager. " +
                        "Lad feltet stå tomt for at beholde det gemte token."), 440),
            _showInWidget,
            Ui.Row(L.T("Tunnel check interval", "Interval for tunnel-tjek"), _interval, Ui.Inline(L.T("minutes", "minutter"))),
            _serviceChecks,
            _autoUpdate,
            _updateAll,
            Ui.Row(L.T("Only these hosts\n(one per line)", "Kun disse hosts\n(én pr. linje)"), _hosts),
            Ui.Row("", _test, _testResult)
        });

        Add(Ui.Heading(L.T("Cloudflare (optional)", "Cloudflare (valgfrit)")),
            Ui.Help(L.T("If you host a website or server at home with DNS at Cloudflare, the app can update your A records when your external IP changes, " +
                        "and show whether your Cloudflare Tunnels are up. " +
                        "If you don't use Cloudflare, just skip this step – everything else works without it.",
                        "Har du en hjemmeside eller server hjemme med DNS hos Cloudflare, kan appen opdatere dine A-records, når din eksterne IP skifter, " +
                        "og vise om dine Cloudflare Tunnels er oppe. " +
                        "Bruger du ikke Cloudflare, så spring bare dette trin over – alt andet virker uden.")),
            _enabled,
            _details);

        _enabled.CheckedChanged += (_, _) => _details.Enabled = _enabled.Checked;
        _updateAll.CheckedChanged += (_, _) => _hosts.Enabled = !_updateAll.Checked;
        _deleteToken.Click += (_, _) =>
        {
            if (MessageBox.Show(this, L.T("Delete the saved Cloudflare token?", "Slet det gemte Cloudflare-token?"), "Cloudflare", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            CredentialStore.DeleteToken();
            UpdateTokenStatus();
        };
        _test.Click += async (_, _) => await TestAsync();
    }

    private void UpdateTokenStatus()
    {
        var has = !string.IsNullOrWhiteSpace(CredentialStore.ReadToken());
        _tokenStatus.Text = has ? L.T("✓ Token saved", "✓ Token gemt") : L.T("No token saved", "Intet token gemt");
        _tokenStatus.ForeColor = has ? Ui.Ok : Ui.Muted;
        _deleteToken.Visible = has;
    }

    private async Task TestAsync()
    {
        var token = string.IsNullOrWhiteSpace(_token.Text) ? CredentialStore.ReadToken() : _token.Text.Trim();
        if (string.IsNullOrWhiteSpace(_zoneId.Text) || string.IsNullOrWhiteSpace(token))
        {
            _testResult.Text = L.T("Fill in the Zone ID and token", "Udfyld Zone ID og token");
            _testResult.ForeColor = Ui.Warn;
            return;
        }
        _test.Enabled = false;
        _testResult.Text = L.T("Testing…", "Tester…");
        _testResult.ForeColor = Ui.Muted;
        try
        {
            var records = await CloudflareClient.GetRecordsAsync(_zoneId.Text.Trim(), token, "A");
            _testResult.Text = L.T($"✓ Connected – {records.Length} A records", $"✓ Forbundet – {records.Length} A-records");
            _testResult.ForeColor = Ui.Ok;

            var account = _accountId.Text.Trim();
            if (account.Length > 0)
            {
                try
                {
                    var tunnels = await CloudflareClient.CountTunnelsAsync(account, token);
                    _testResult.Text += $", {tunnels} tunnels";
                }
                catch (Exception ex)
                {
                    _testResult.Text += L.T(" – but tunnels could not be read: ", " – men tunnels kunne ikke læses: ") + ex.Message;
                    _testResult.ForeColor = Ui.Warn;
                }
            }
        }
        catch (Exception ex)
        {
            _testResult.Text = L.T("Error: ", "Fejl: ") + ex.Message;
            _testResult.ForeColor = Ui.Error;
        }
        finally
        {
            _test.Enabled = true;
        }
    }

    public override void LoadFrom(AppSettings s)
    {
        _enabled.Checked = s.CloudflareEnabled;
        _details.Enabled = s.CloudflareEnabled;
        _zoneId.Text = s.ZoneId ?? "";
        _accountId.Text = s.CloudflareAccountId ?? "";
        _interval.Value = Math.Clamp(s.CloudflareRefreshMinutes, 1, 60);
        _showInWidget.Checked = s.ShowCloudflare;
        _serviceChecks.Checked = s.ServiceChecksEnabled;
        _autoUpdate.Checked = s.CloudflareAutoUpdate;
        _updateAll.Checked = s.UpdateAllARecords;
        _hosts.Enabled = !s.UpdateAllARecords;
        _hosts.Text = string.Join(Environment.NewLine, s.IncludedHosts);
        UpdateTokenStatus();
    }

    public override string? SaveTo(AppSettings s)
    {
        if (!_managedPlugin) s.SetPluginEnabled("cloudflare", _enabled.Checked);
        s.CloudflareEnabled = _enabled.Checked;
        if (!_enabled.Checked) return null;

        var zone = _zoneId.Text.Trim();
        var newToken = _token.Text.Trim();
        if (zone.Length == 0) return L.T("Fill in the Cloudflare Zone ID – or turn Cloudflare off.", "Udfyld Cloudflare Zone ID – eller slå Cloudflare fra.");
        if (newToken.Length == 0 && string.IsNullOrWhiteSpace(CredentialStore.ReadToken()))
            return L.T("Paste a Cloudflare API token – or turn Cloudflare off.", "Indsæt et Cloudflare API-token – eller slå Cloudflare fra.");

        s.ZoneId = zone;
        s.CloudflareAccountId = _accountId.Text.Trim() is { Length: > 0 } account ? account : null;
        s.CloudflareRefreshMinutes = (int)_interval.Value;
        s.ShowCloudflare = _showInWidget.Checked;
        s.ServiceChecksEnabled = _serviceChecks.Checked;
        s.CloudflareAutoUpdate = _autoUpdate.Checked;
        s.UpdateAllARecords = _updateAll.Checked;
        s.IncludedHosts = _updateAll.Checked
            ? Array.Empty<string>()
            : _hosts.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(h => h.Trim()).Where(h => h.Length > 0).ToArray();

        if (newToken.Length > 0)
        {
            CredentialStore.WriteToken(newToken);
            _token.Clear();
            UpdateTokenStatus();
            Logger.Info("Cloudflare token updated in Credential Manager.");
        }
        return null;
    }
}

// ---------------------------------------------------------------------------

internal sealed class ProxmoxPage : SettingsPage
{
    private readonly CheckBox _enabled = Ui.Check(L.T("I use Proxmox VE and want to see my server in the widget", "Jeg bruger Proxmox VE og vil se min server i widgetten"));
    private readonly NumericUpDown _interval = new() { Width = 70, Minimum = 5, Maximum = 600 };
    private readonly CheckBox _showInWidget = Ui.Check(L.T("Show Proxmox in the widget", "Vis Proxmox i widgetten"));
    private readonly FlowLayoutPanel _details;
    private readonly TextBox _url = new() { Width = 290, PlaceholderText = L.T("e.g. 192.168.1.50", "fx 192.168.1.50") };
    private readonly TextBox _tokenId = new() { Width = 290, PlaceholderText = L.T("e.g. widget@pve!widget", "fx widget@pve!widget") };
    private readonly TextBox _secret = new() { Width = 290, UseSystemPasswordChar = true };
    private readonly Label _secretStatus = Ui.Inline("");
    private readonly Button _deleteSecret = new() { Text = L.T("Delete", "Slet"), AutoSize = true };
    private readonly CheckBox _selfSigned = Ui.Check(L.T("Accept Proxmox's self-signed certificate", "Acceptér Proxmox' selvsignerede certifikat"));
    private readonly Label _certStatus = Ui.Inline("");
    private readonly Button _forgetCert = new() { Text = L.T("Forget certificate", "Glem certifikat"), AutoSize = true };
    private readonly Button _test = new() { Text = L.T("Test connection", "Test forbindelse"), AutoSize = true };
    private readonly Label _testResult = new() { AutoSize = true, MaximumSize = new Size(300, 0), ForeColor = Ui.Muted, Padding = new Padding(0, 5, 0, 0) };
    private string? _thumbprint;
    private string _loadedUrl = "";

    public override string Title => "Proxmox";
    public override string Glyph => "\uE977";

    private bool _managedPlugin;

    public override void UsePluginActivation()
    {
        _managedPlugin = true;
        _enabled.Visible = false;
        _enabled.Checked = true;
        _details.Enabled = true;
    }

    public ProxmoxPage()
    {
        _details = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(20, 4, 0, 0) };
        _details.Controls.AddRange(new Control[]
        {
            _showInWidget,
            Ui.Row(L.T("Update interval", "Opdateringsinterval"), _interval, Ui.Inline(L.T("seconds (while shown)", "sekunder (mens den vises)"))),
            Ui.Row(L.T("Address", "Adresse"), _url),
            Ui.Help(L.T("The address you open Proxmox on. Port 8006 is used unless you type another.", "Adressen du åbner Proxmox på. Port 8006 bruges, hvis du ikke skriver en anden."), 440),
            Ui.Row(L.T("Token ID", "Token-id"), _tokenId),
            Ui.Row(L.T("Secret", "Hemmelig nøgle"), _secret),
            Ui.Row("", _secretStatus, _deleteSecret),
            Ui.Help(L.T("Create an API token in Proxmox under Datacenter → Permissions → API Tokens. Give it the role PVEAuditor on / " +
                        "to see the status – and also PVEVMUser if you want to start, shut down and reboot machines from the widget. " +
                        "The secret is stored in Windows Credential Manager. Leave the field empty to keep the saved secret.",
                        "Opret et API-token i Proxmox under Datacenter → Permissions → API Tokens. Giv det rollen PVEAuditor på / " +
                        "for at se status – og desuden PVEVMUser, hvis du vil starte, lukke og genstarte maskiner fra widgetten. " +
                        "Nøglen gemmes i Windows Credential Manager. Lad feltet stå tomt for at beholde den gemte nøgle."), 440),
            _selfSigned,
            Ui.Row("", _certStatus, _forgetCert),
            Ui.Help(L.T("The first time, the certificate's fingerprint is remembered. If the certificate changes later, the connection is refused " +
                        "until you press \"Forget certificate\" – so nobody can pretend to be your server.",
                        "Første gang huskes certifikatets fingeraftryk. Skifter certifikatet senere, afvises forbindelsen, " +
                        "indtil du trykker \"Glem certifikat\" – så ingen kan udgive sig for at være din server."), 440),
            Ui.Row("", _test, _testResult)
        });

        Add(Ui.Heading(L.T("Proxmox (optional)", "Proxmox (valgfrit)")),
            Ui.Help(L.T("See CPU, RAM and storage on your Proxmox server, which VMs and containers are running, and start, shut down or reboot them with a click. " +
                        "The widget only fetches data while it is visible.",
                        "Se CPU, RAM og lager på din Proxmox-server, hvilke VM'er og containere der kører, og start, luk eller genstart dem med et klik. " +
                        "Widgetten henter kun data, mens den er synlig.")),
            _enabled,
            _details);

        _enabled.CheckedChanged += (_, _) => _details.Enabled = _enabled.Checked;
        _deleteSecret.Click += (_, _) =>
        {
            if (MessageBox.Show(this, L.T("Delete the saved Proxmox secret?", "Slet den gemte Proxmox-nøgle?"), "Proxmox", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            CredentialStore.DeleteProxmoxSecret();
            UpdateSecretStatus();
        };
        _forgetCert.Click += (_, _) =>
        {
            _thumbprint = null;
            UpdateCertStatus();
        };
        _test.Click += async (_, _) => await TestAsync();
    }

    private void UpdateSecretStatus()
    {
        var has = !string.IsNullOrWhiteSpace(CredentialStore.ReadProxmoxSecret());
        _secretStatus.Text = has ? L.T("✓ Secret saved", "✓ Nøgle gemt") : L.T("No secret saved", "Ingen nøgle gemt");
        _secretStatus.ForeColor = has ? Ui.Ok : Ui.Muted;
        _deleteSecret.Visible = has;
    }

    private void UpdateCertStatus()
    {
        _certStatus.Text = _thumbprint is { Length: > 16 } t ? L.T("Remembered: ", "Husket: ") + t[..16] + "…" : L.T("No certificate remembered yet", "Intet certifikat husket endnu");
        _forgetCert.Visible = _thumbprint != null;
    }

    private async Task TestAsync()
    {
        var secret = string.IsNullOrWhiteSpace(_secret.Text) ? CredentialStore.ReadProxmoxSecret() : _secret.Text.Trim();
        if (string.IsNullOrWhiteSpace(_url.Text) || string.IsNullOrWhiteSpace(_tokenId.Text) || string.IsNullOrWhiteSpace(secret))
        {
            _testResult.Text = L.T("Fill in the address, token ID and secret", "Udfyld adresse, token-id og nøgle");
            _testResult.ForeColor = Ui.Warn;
            return;
        }

        var probe = new AppSettings
        {
            ProxmoxEnabled = true,
            ProxmoxUrl = _url.Text.Trim(),
            ProxmoxTokenId = _tokenId.Text.Trim(),
            ProxmoxAllowSelfSigned = _selfSigned.Checked,
            ProxmoxCertThumbprint = UrlChanged ? null : _thumbprint
        };
        _test.Enabled = false;
        _testResult.Text = L.T("Testing…", "Tester…");
        _testResult.ForeColor = Ui.Muted;
        try
        {
            _testResult.Text = "✓ " + await new ProxmoxService().TestAsync(probe, secret);
            _testResult.ForeColor = Ui.Ok;
            _thumbprint = probe.ProxmoxCertThumbprint;
            _loadedUrl = _url.Text.Trim();
            UpdateCertStatus();
        }
        catch (Exception ex)
        {
            _testResult.Text = L.T("Error: ", "Fejl: ") + (ex is System.Net.Http.HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException }
                ? L.T("the certificate was refused", "certifikatet blev afvist") + (_selfSigned.Checked
                    ? L.T(" – it changed since last time. Press \"Forget certificate\" if that is expected.", " – det er ændret siden sidst. Tryk \"Glem certifikat\", hvis det er forventet.")
                    : L.T(". Turn on the self-signed certificate.", ". Slå selvsigneret certifikat til."))
                : ex.InnerException?.Message ?? ex.Message);
            _testResult.ForeColor = Ui.Error;
        }
        finally
        {
            _test.Enabled = true;
        }
    }

    private bool UrlChanged => !string.Equals(ProxmoxService.Normalize(_url.Text), ProxmoxService.Normalize(_loadedUrl), StringComparison.OrdinalIgnoreCase);

    public override void LoadFrom(AppSettings s)
    {
        _enabled.Checked = s.ProxmoxEnabled;
        _details.Enabled = s.ProxmoxEnabled;
        _interval.Value = Math.Clamp(s.ProxmoxRefreshSeconds, 5, 600);
        _showInWidget.Checked = s.ShowProxmox;
        _url.Text = s.ProxmoxUrl ?? "";
        _loadedUrl = _url.Text;
        _tokenId.Text = s.ProxmoxTokenId ?? "";
        _selfSigned.Checked = s.ProxmoxAllowSelfSigned;
        _thumbprint = s.ProxmoxCertThumbprint;
        UpdateSecretStatus();
        UpdateCertStatus();
    }

    public override string? SaveTo(AppSettings s)
    {
        if (!_managedPlugin) s.SetPluginEnabled("proxmox", _enabled.Checked);
        s.ProxmoxEnabled = _enabled.Checked;
        s.ProxmoxRefreshSeconds = (int)_interval.Value;
        s.ShowProxmox = _showInWidget.Checked;
        if (!_enabled.Checked) return null;

        var url = _url.Text.Trim();
        var tokenId = _tokenId.Text.Trim();
        var secret = _secret.Text.Trim();
        if (url.Length == 0) return L.T("Fill in the address of your Proxmox server – or turn Proxmox off.", "Udfyld adressen på din Proxmox-server – eller slå Proxmox fra.");
        if (!tokenId.Contains('!')) return L.T("The token ID must look like user@realm!name, e.g. widget@pve!widget.", "Token-id skal have formen bruger@realm!navn, fx widget@pve!widget.");
        if (secret.Length == 0 && string.IsNullOrWhiteSpace(CredentialStore.ReadProxmoxSecret()))
            return L.T("Paste the secret for the Proxmox token – or turn Proxmox off.", "Indsæt den hemmelige nøgle til Proxmox-tokenet – eller slå Proxmox fra.");

        s.ProxmoxUrl = ProxmoxService.Normalize(url);
        s.ProxmoxTokenId = tokenId;
        s.ProxmoxAllowSelfSigned = _selfSigned.Checked;
        s.ProxmoxCertThumbprint = UrlChanged ? null : _thumbprint;

        if (secret.Length > 0)
        {
            CredentialStore.WriteProxmoxSecret(secret);
            _secret.Clear();
            UpdateSecretStatus();
            Logger.Info("Proxmox secret updated in Credential Manager.");
        }
        return null;
    }
}

// ---------------------------------------------------------------------------

internal sealed class HomeAssistantPage : SettingsPage
{
    /// <summary>Cap on how tall the Home Assistant section can get. The section can be collapsed.</summary>
    private const int MaxEntities = 20;

    // The user presses the button and can wait; a large installation takes a while to answer /api/states
    private static readonly HttpClient Http = HttpClientFactory.Create(TimeSpan.FromSeconds(30));

    private readonly CheckBox _enabled = Ui.Check(L.T("I use Home Assistant and want to control lights from the widget", "Jeg bruger Home Assistant og vil styre lys fra widgetten"));
    private readonly NumericUpDown _interval = new() { Width = 70, Minimum = 5, Maximum = 600 };
    private readonly CheckBox _showInWidget = Ui.Check(L.T("Show Home Assistant – lights and sensors – in the widget", "Vis Home Assistant – lys og sensorer – i widgetten"));
    private readonly FlowLayoutPanel _details;
    private readonly TextBox _url = new() { Width = 290 };
    private readonly TextBox _token = new() { Width = 290, UseSystemPasswordChar = true };
    private readonly Label _tokenStatus = Ui.Inline("");
    private readonly Button _deleteToken = new() { Text = L.T("Delete", "Slet"), AutoSize = true };
    private readonly Button _load = new() { Text = L.T("Load entities", "Hent enheder"), AutoSize = true };

    /// <summary>Wraps, so a long error message does not run past the window edge.</summary>
    private readonly Label _loadStatus = new()
    {
        AutoSize = true,
        MaximumSize = new Size(Ui.ContentWidth, 0),
        ForeColor = Ui.Muted,
        Margin = new Padding(0, 2, 0, 4)
    };
    private readonly TextBox _dashboard = new() { Width = 290 };
    private readonly ComboBox _domain = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _search = new() { Width = 200, PlaceholderText = L.T("Search…", "Søg…") };
    private readonly CheckedListBox _list = new() { Width = 440, Height = 160, CheckOnClick = true, IntegralHeight = false };
    private readonly Label _selectedStatus = Ui.Inline("");

    /// <summary>Selected entities in the order the user ticked them – also used in the widget.</summary>
    private readonly List<string> _selected = new();
    private IReadOnlyList<HaEntity> _all = Array.Empty<HaEntity>();
    private bool _updatingList;

    public override string Title => "Home Assistant";
    public override string Glyph => "\uE80F";

    private bool _managedPlugin;

    public override void UsePluginActivation()
    {
        _managedPlugin = true;
        _enabled.Visible = false;
        _enabled.Checked = true;
        _details.Enabled = true;
    }

    public HomeAssistantPage()
    {
        _domain.Items.AddRange(new object[] { L.T("Lights and switches", "Lys og kontakter"), L.T("Sensors", "Sensorer"), L.T("All entities", "Alle enheder") });
        _domain.SelectedIndex = 0;

        _details = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(20, 4, 0, 0) };
        _details.Controls.AddRange(new Control[]
        {
            _showInWidget,
            Ui.Row(L.T("Update interval", "Opdateringsinterval"), _interval, Ui.Inline(L.T("seconds (while shown)", "sekunder (mens den vises)"))),
            Ui.Row(L.T("Address", "Adresse"), _url),
            Ui.Help(L.T("E.g. http://192.168.0.10:8123 or https://home.example.com – the address you open Home Assistant on yourself.",
                        "Fx http://192.168.0.10:8123 eller https://hjem.eksempel.dk – den adresse du selv åbner Home Assistant på."), 440),
            Ui.Row(L.T("Access token", "Adgangstoken"), _token),
            Ui.Row("", _tokenStatus, _deleteToken),
            Ui.Help(L.T("Create a long-lived token in Home Assistant under your profile → Security → Long-lived access tokens. " +
                        "The token is stored in Windows Credential Manager. Leave the field empty to keep the saved token.",
                        "Opret et langtidstoken i Home Assistant under din profil → Sikkerhed → Langtids-adgangstokens. " +
                        "Tokenet gemmes i Windows Credential Manager. Lad feltet stå tomt for at beholde det gemte token."), 440),
            Ui.Row("", _load),
            _loadStatus,
            Ui.Section(L.T("The panel", "Panelet")),
            Ui.Row("Dashboard", _dashboard),
            Ui.Help(L.T("Type the path to one of your Home Assistant dashboards, e.g. /test-panel, to show it in the panel " +
                        "instead of the list of switches. You sign in to the panel the first time, and the login is remembered afterwards. " +
                        "Leave the field empty to use the list below.",
                        "Skriv stien til et af dine Home Assistant-dashboards, fx /test-panel, for at vise det i panelet " +
                        "i stedet for listen med kontakter. Du logger ind i panelet første gang, og loginnet huskes bagefter. " +
                        "Lad feltet stå tomt for at bruge listen herunder."), 440),
            Ui.Section(L.T("Entities", "Enheder")),
            Ui.Help(L.T("The entities are used for the summary in the widget (e.g. \"5 on\") – and for the list in the panel " +
                        "if you have not chosen a dashboard.",
                        "Enhederne bruges til resuméet i widgetten (fx \"5 tændt\") – og til listen i panelet, " +
                        "hvis du ikke har valgt et dashboard."), 440),
            Ui.Row(L.T("Show", "Vis"), _domain, _search),
            _list,
            _selectedStatus
        });

        Add(Ui.Heading(L.T("Home Assistant (optional)", "Home Assistant (valgfrit)")),
            Ui.Help(L.T("The widget can show selected lights, switches and sensors from your Home Assistant – and switch them " +
                        "on and off with a click. If you don't use Home Assistant, just leave it off.",
                        "Widgetten kan vise udvalgte lamper, kontakter og sensorer fra din Home Assistant – og tænde og slukke " +
                        "dem med et klik. Bruger du ikke Home Assistant, kan du bare lade det være slået fra.")),
            _enabled,
            _details);

        _enabled.CheckedChanged += (_, _) => _details.Enabled = _enabled.Checked;
        _deleteToken.Click += (_, _) =>
        {
            if (MessageBox.Show(this, L.T("Delete the saved Home Assistant token?", "Slet det gemte Home Assistant-token?"), "Home Assistant",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            CredentialStore.DeleteHomeAssistantToken();
            UpdateTokenStatus();
        };
        _load.Click += async (_, _) => await LoadEntitiesAsync();
        _domain.SelectedIndexChanged += (_, _) => RefreshList();
        _search.TextChanged += (_, _) => RefreshList();
        _list.ItemCheck += OnItemCheck;
    }

    private void OnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_updatingList || _list.Items[e.Index] is not EntityItem item) return;

        if (e.NewValue == CheckState.Checked)
        {
            if (_selected.Count >= MaxEntities)
            {
                e.NewValue = CheckState.Unchecked;
                MessageBox.Show(this, L.T($"The widget can show up to {MaxEntities} entities. Remove one before you add another.", $"Widgetten kan vise op til {MaxEntities} enheder. Fjern en, før du tilføjer en ny."),
                                "Home Assistant", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!_selected.Contains(item.Entity.EntityId)) _selected.Add(item.Entity.EntityId);
        }
        else
        {
            _selected.Remove(item.Entity.EntityId);
        }

        // ItemCheck fires before the check is set, so the counter is updated afterwards
        BeginInvoke(UpdateSelectedStatus);
    }

    private void UpdateSelectedStatus()
    {
        _selectedStatus.Text = _selected.Count == 0
            ? L.T("No entities selected – the section is not shown in the widget", "Ingen enheder valgt – sektionen vises ikke i widgetten")
            : L.T($"{_selected.Count} of {MaxEntities} selected", $"{_selected.Count} af {MaxEntities} valgt");
        _selectedStatus.ForeColor = _selected.Count == 0 ? Ui.Muted : Ui.Ok;
    }

    private async Task LoadEntitiesAsync()
    {
        var url = _url.Text.Trim();
        var token = _token.Text.Trim().Length > 0 ? _token.Text.Trim() : CredentialStore.ReadHomeAssistantToken();
        if (url.Length == 0 || string.IsNullOrWhiteSpace(token))
        {
            _loadStatus.Text = L.T("Fill in the address and token", "Udfyld adresse og token");
            _loadStatus.ForeColor = Ui.Warn;
            return;
        }

        _load.Enabled = false;
        _loadStatus.Text = L.T("Loading…", "Henter…");
        _loadStatus.ForeColor = Ui.Muted;
        try
        {
            _all = await HomeAssistantService.FetchAllAsync(Http, url, token!);
            _loadStatus.Text = L.T($"✓ Connected – {_all.Count} entities", $"✓ Forbundet – {_all.Count} enheder");
            _loadStatus.ForeColor = Ui.Ok;
            RefreshList();
        }
        catch (Exception ex)
        {
            var message = HomeAssistantService.Describe(ex);
            if (HomeAssistantService.LooksUnreachable(ex))
            {
                // If the address is your own public name, many routers cannot send the connection
                // out and back home again (NAT hairpin). The local address works.
                message += L.T(". If you use an outside address, try the local one instead, e.g. http://192.168.0.10:8123 – " +
                               "many routers cannot reach your own public address from inside.",
                               ". Bruger du en udefra-adresse, så prøv den lokale i stedet, fx http://192.168.0.10:8123 – " +
                               "mange routere kan ikke nå din egen offentlige adresse indefra.");
            }
            _loadStatus.Text = L.T("Error: ", "Fejl: ") + message;
            _loadStatus.ForeColor = Ui.Error;
        }
        finally
        {
            _load.Enabled = true;
        }
    }

    private void RefreshList()
    {
        var search = _search.Text.Trim();
        var matches = _all.Where(InDomainFilter)
                          .Where(e => search.Length == 0
                                      || e.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                                      || e.EntityId.Contains(search, StringComparison.OrdinalIgnoreCase))
                          .ToList();

        // Selected entities must always be visible and removable, whatever the filter
        foreach (var id in _selected)
        {
            if (matches.Any(m => m.EntityId == id)) continue;
            var known = _all.FirstOrDefault(e => e.EntityId == id);
            // Renamed or deleted in Home Assistant: show it anyway, so the tick can be removed
            matches.Insert(0, known ?? new HaEntity(id, id + L.T(" (no longer exists)", " (findes ikke længere)"), "unavailable", null, null));
        }

        _updatingList = true;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var entity in matches)
            {
                _list.Items.Add(new EntityItem(entity), _selected.Contains(entity.EntityId));
            }
        }
        finally
        {
            _list.EndUpdate();
            _updatingList = false;
        }
        UpdateSelectedStatus();
    }

    private bool InDomainFilter(HaEntity entity) => _domain.SelectedIndex switch
    {
        0 => entity.CanToggle,
        1 => entity.Domain is "sensor" or "binary_sensor",
        _ => true
    };

    public override async Task OnFirstShownAsync()
    {
        if (_enabled.Checked && _url.Text.Trim().Length > 0 && !string.IsNullOrWhiteSpace(CredentialStore.ReadHomeAssistantToken()))
        {
            await LoadEntitiesAsync();
        }
    }

    public override void LoadFrom(AppSettings s)
    {
        _enabled.Checked = s.HomeAssistantEnabled;
        _details.Enabled = s.HomeAssistantEnabled;
        _interval.Value = Math.Clamp(s.HomeAssistantRefreshSeconds, 5, 600);
        _showInWidget.Checked = s.ShowHomeAssistant;
        _url.Text = s.HomeAssistantUrl ?? "";
        _dashboard.Text = s.HomeAssistantDashboardPath ?? "";
        _selected.Clear();
        _selected.AddRange(s.HomeAssistantEntities);

        // Until the entities are loaded, the selected ones are shown with their entity id
        _all = _selected.Select(id => new HaEntity(id, id, "unknown", null, null)).ToList();
        RefreshList();
        UpdateTokenStatus();
    }

    private void UpdateTokenStatus()
    {
        var has = !string.IsNullOrWhiteSpace(CredentialStore.ReadHomeAssistantToken());
        _tokenStatus.Text = has ? L.T("✓ Token saved", "✓ Token gemt") : L.T("No token saved", "Intet token gemt");
        _tokenStatus.ForeColor = has ? Ui.Ok : Ui.Muted;
        _deleteToken.Visible = has;
    }

    public override string? SaveTo(AppSettings s)
    {
        if (!_managedPlugin) s.SetPluginEnabled("ha", _enabled.Checked);
        s.HomeAssistantEnabled = _enabled.Checked;
        s.HomeAssistantRefreshSeconds = (int)_interval.Value;
        s.ShowHomeAssistant = _showInWidget.Checked;
        if (!_enabled.Checked)
        {
            s.HomeAssistantEntities = _selected.ToArray();
            return null;
        }

        var url = _url.Text.Trim();
        var newToken = _token.Text.Trim();
        if (url.Length == 0) return L.T("Fill in the address of your Home Assistant – or turn Home Assistant off.", "Udfyld adressen på din Home Assistant – eller slå Home Assistant fra.");
        if (newToken.Length == 0 && string.IsNullOrWhiteSpace(CredentialStore.ReadHomeAssistantToken()))
            return L.T("Paste a long-lived access token from Home Assistant – or turn Home Assistant off.", "Indsæt et langtids-adgangstoken fra Home Assistant – eller slå Home Assistant fra.");

        s.HomeAssistantUrl = HomeAssistantService.Normalize(url);
        s.HomeAssistantDashboardPath = _dashboard.Text.Trim() is { Length: > 0 } path ? path : null;
        s.HomeAssistantEntities = _selected.Take(MaxEntities).ToArray();

        if (newToken.Length > 0)
        {
            CredentialStore.WriteHomeAssistantToken(newToken);
            _token.Clear();
            UpdateTokenStatus();
            Logger.Info("Home Assistant token updated in Credential Manager.");
        }
        return null;
    }

    private sealed class EntityItem
    {
        public HaEntity Entity { get; }

        public EntityItem(HaEntity entity) => Entity = entity;

        public override string ToString() =>
            Entity.Name.Equals(Entity.EntityId, StringComparison.Ordinal)
                ? Entity.EntityId
                : $"{Entity.Name}     ({Entity.EntityId})";
    }
}

// ---------------------------------------------------------------------------

internal sealed class AudioPage : SettingsPage
{
    private readonly CheckBox _show = Ui.Check(L.T("Show audio devices at the bottom of the widget", "Vis lydenheder nederst i widgetten"));
    private readonly DataGridView _grid = new()
    {
        Width = 440,
        Height = 180,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        EditMode = DataGridViewEditMode.EditOnEnter,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        Margin = new Padding(0, 4, 0, 4)
    };
    private readonly Button _up = new() { Text = L.T("Move up", "Flyt op"), AutoSize = true };
    private readonly Button _down = new() { Text = L.T("Move down", "Flyt ned"), AutoSize = true };
    private readonly Button _reload = new() { Text = L.T("Refresh the list", "Opdater listen"), AutoSize = true };
    private readonly ComboBox _standard = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Panel _batterySwatch = new() { Size = new Size(26, 22), BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand };
    private readonly Button _batteryPick = new() { Text = L.T("Choose colour …", "Vælg farve …"), AutoSize = true };
    private readonly Button _batteryReset = new() { Text = L.T("Default (green)", "Standard (grøn)"), AutoSize = true };
    private readonly CheckBox _batteryWarn = Ui.Check(L.T("Turn orange at 30 % and red at 15 %", "Skift til orange ved 30 % og rød ved 15 %"));
    private readonly CheckBox _batteryAlert = Ui.Check(L.T("Notify me when the battery is running low", "Giv besked, når batteriet er ved at løbe tør"));
    private readonly NumericUpDown _batteryLow = new() { Width = 60, Minimum = 5, Maximum = 50 };
    private readonly NumericUpDown _batteryCritical = new() { Width = 60, Minimum = 1, Maximum = 30 };
    private readonly CheckBox _switchMic = Ui.Check(L.T("Also switch microphone when you switch audio output", "Skift også mikrofon, når du skifter lydudgang"));
    private readonly CheckBox _showMic = Ui.Check(L.T("Show the microphone in the widget – click it to mute and unmute", "Vis mikrofonen i widgetten – klik på den for at slå den fra og til"));
    private readonly CheckBox _showVolume = Ui.Check(L.T("Show the volume on the buttons – scroll over a button to adjust", "Vis lydstyrken på knapperne – scroll over en knap for at justere"));
    private readonly NumericUpDown _volumeStep = new() { Width = 60, Minimum = 1, Maximum = 20 };
    private Color? _batteryColor;
    private static string MicAutomaticPrefix => L.T("Automatic", "Automatisk");
    private static string MicNone => L.T("Don't switch", "Skift ikke");
    private static readonly Color DefaultBatteryColor = Color.FromArgb(63, 185, 80);
    private readonly AudioService _audio = new();
    private readonly Dictionary<string, string> _names = new();
    private AppSettings? _loaded;
    /// <summary>Did the widget show "all devices" when loaded, and has the user not touched the ticks or the order?</summary>
    private bool _automatic;
    private bool _filling;

    public override string Title => L.T("Audio", "Lyd");
    public override string Glyph => "\uE767";

    private sealed record StandardItem(string? Id, string Text)
    {
        public override string ToString() => Text;
    }

    public AudioPage()
    {
        _grid.Width = Ui.ContentWidth;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = L.T("Show", "Vis"), FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Name in the widget", "Navn i widgetten"), FillWeight = 32 });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = L.T("Microphone", "Mikrofon"),
            FillWeight = 40,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Flat
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = L.T("Device in Windows", "Enhed i Windows"), FillWeight = 42, ReadOnly = true });
        _grid.Columns[3].DefaultCellStyle.ForeColor = Ui.Muted;
        _switchMic.CheckedChanged += (_, _) => _grid.Columns[2].Visible = _switchMic.Checked;
        _showVolume.CheckedChanged += (_, _) => _volumeStep.Enabled = _showVolume.Checked;
        _batteryAlert.CheckedChanged += (_, _) => _batteryLow.Enabled = _batteryCritical.Enabled = _batteryAlert.Checked;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellEndEdit += (_, _) => FillStandard();
        _grid.CellValueChanged += (_, e) =>
        {
            if (!_filling && e.ColumnIndex == 0) _automatic = false;
        };
        _up.Click += (_, _) => MoveRow(-1);
        _down.Click += (_, _) => MoveRow(1);
        _reload.Click += (_, _) =>
        {
            if (_loaded == null) return;
            var current = new AppSettings();
            SaveTo(current); // keep what the user has set so far
            Fill(current);
        };
        _batteryPick.Click += (_, _) => PickBatteryColor();
        _batterySwatch.Click += (_, _) => PickBatteryColor();
        _batteryReset.Click += (_, _) => SetBatteryColor(null);

        Add(Ui.Heading(L.T("Audio", "Lyd")),
            Ui.Help(L.T("Switch audio output with one click in the widget – e.g. between headphones and the monitor's speakers. " +
                        "The chosen device becomes the Windows default for both playback and calls.",
                        "Skift lydudgang med ét klik i widgetten – fx mellem høretelefoner og skærmens højttalere. " +
                        "Den valgte enhed bliver Windows' standard for både afspilning og opkald.")),
            _show,
            Ui.Section(L.T("Devices", "Enheder")),
            Ui.Help(L.T("Tick the devices to show, and give them a short name if you like. The order is the same as in the widget.",
                        "Sæt flueben ved de enheder, der skal vises, og giv dem gerne et kort navn. Rækkefølgen er den samme som i widgetten."), 440),
            _grid,
            Ui.Row("", _up, _down, _reload),
            _switchMic,
            Ui.Help(L.T("The Microphone column decides which microphone comes along. \"Automatic\" picks the microphone in the same " +
                        "device – e.g. the headset's own. For outputs without a microphone, like the monitor, it switches back to a standalone " +
                        "microphone (e.g. the webcam) when you come from the headset.",
                        "Kolonnen Mikrofon bestemmer, hvilken mikrofon der følger med. \"Automatisk\" vælger mikrofonen i samme " +
                        "apparat – fx headsettets egen. For udgange uden mikrofon, som skærmen, skiftes tilbage til en selvstændig " +
                        "mikrofon (fx webcammet), når du kommer fra headsettet."), 440),
            Ui.Section(L.T("Default device", "Standardenhed")),
            Ui.Row(L.T("Default device", "Standardenhed"), _standard),
            Ui.Help(L.T("The default device is chosen when Windows starts, and when the active device disappears – e.g. when you switch off " +
                        "your wireless headphones. It is marked with a star in the widget.",
                        "Standardenheden vælges, når Windows starter, og når den aktive enhed forsvinder – fx når du slukker " +
                        "dine trådløse høretelefoner. Den er markeret med en stjerne i widgetten."), 440),
            Ui.Section(L.T("Microphone and volume", "Mikrofon og lydstyrke")),
            _showMic,
            _showVolume,
            Ui.Row(L.T("Step per scroll notch", "Trin pr. scroll-hak"), _volumeStep, Ui.Inline("%")),
            Ui.Section(L.T("Headset battery", "Headsetbatteri")),
            Ui.Help(L.T("Wireless headsets whose battery can be read (e.g. Corsair HS80) fill their button from the bottom by battery level.",
                        "Trådløse headsets, hvor batteriet kan læses (fx Corsair HS80), fyldes nedefra i deres knap efter batteriniveauet."), 440),
            Ui.Row(L.T("Colour", "Farve"), _batterySwatch, _batteryPick, _batteryReset),
            _batteryWarn,
            _batteryAlert,
            Ui.Row(L.T("First message at", "Første besked ved"), _batteryLow, Ui.Inline("%")),
            Ui.Row(L.T("Last message at", "Sidste besked ved"), _batteryCritical, Ui.Inline(L.T("% – with sound", "% – med lyd"))),
            Ui.Help(L.T("The message has a button that moves the sound to the default device (or the speakers), so you don't lose the sound in the middle of something.",
                        "Beskeden har en knap, der flytter lyden til standardenheden (eller højttalerne), så du ikke mister lyden midt i noget."), 440));
    }

    /// <summary>The choices in the Microphone column for one output, and which microphone id each means ("" = don't switch, null = automatic).</summary>
    private List<(string Label, string? MicId)> MicChoices(string outputId, string? selected)
    {
        var auto = _audio.AutomaticMic(outputId) is { } mic
            ? $"{MicAutomaticPrefix} ({mic.Name})"
            : $"{MicAutomaticPrefix} ({L.T("don't switch", "skift ikke")})";
        var choices = new List<(string, string?)> { (auto, null), (MicNone, "") };
        choices.AddRange(_audio.Microphones.Select(m => (m.FullName, (string?)m.Id)));
        if (!string.IsNullOrEmpty(selected) && _audio.Microphones.All(m => m.Id != selected))
            choices.Add((L.T("(microphone not connected)", "(mikrofon ikke tilsluttet)"), selected));
        return choices;
    }

    private void SetBatteryColor(Color? color)
    {
        _batteryColor = color;
        _batterySwatch.BackColor = color ?? DefaultBatteryColor;
        _batteryReset.Enabled = color != null;
    }

    private void PickBatteryColor()
    {
        using var dialog = new ColorDialog { Color = _batteryColor ?? DefaultBatteryColor, FullOpen = true };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) SetBatteryColor(dialog.Color);
    }

    public override void LoadFrom(AppSettings s)
    {
        _loaded = s;
        _show.Checked = s.ShowAudio;
        _automatic = s.AudioDeviceIds == null;
        SetBatteryColor(BatteryColorSetting.Parse(s.AudioBatteryColor));
        _batteryWarn.Checked = s.AudioBatteryWarn;
        _batteryAlert.Checked = s.AudioBatteryAlert;
        _batteryLow.Value = Math.Clamp(s.AudioBatteryLowLevel, (int)_batteryLow.Minimum, (int)_batteryLow.Maximum);
        _batteryCritical.Value = Math.Clamp(s.AudioBatteryCriticalLevel, (int)_batteryCritical.Minimum, (int)_batteryCritical.Maximum);
        _switchMic.Checked = s.AudioSwitchMic;
        _showMic.Checked = s.AudioShowMic;
        _showVolume.Checked = s.AudioShowVolume;
        _volumeStep.Value = Math.Clamp(s.AudioVolumeStep, (int)_volumeStep.Minimum, (int)_volumeStep.Maximum);
        _grid.Columns[2].Visible = _switchMic.Checked;
        _volumeStep.Enabled = _showVolume.Checked;
        _batteryLow.Enabled = _batteryCritical.Enabled = _batteryAlert.Checked;
        Fill(s);
    }

    private void Fill(AppSettings s)
    {
        _filling = true;
        _audio.Refresh();
        _names.Clear();
        foreach (var kv in s.AudioNames) _names[kv.Key] = kv.Value;
        _grid.Rows.Clear();

        var active = _audio.Devices;
        var ids = s.AudioDeviceIds;
        if (ids != null)
        {
            foreach (var id in ids)
            {
                var d = active.FirstOrDefault(a => a.Id == id);
                AddRow(id, true, d?.Name ?? "", d != null ? d.FullName : L.T("(not connected)", "(ikke tilsluttet)"), s);
            }
        }
        foreach (var d in active.Where(d => ids == null || !ids.Contains(d.Id)))
        {
            AddRow(d.Id, ids == null && !AudioService.HiddenByDefault(d), d.Name, d.FullName, s);
        }
        FillStandard(s.AudioStandardId);
        _filling = false;
    }

    private void AddRow(string id, bool show, string suggested, string full, AppSettings s)
    {
        var name = _names.TryGetValue(id, out var custom) && !string.IsNullOrWhiteSpace(custom) ? custom : suggested;
        var selected = s.AudioMicPairs.TryGetValue(id, out var pair) ? pair : null;
        var choices = MicChoices(id, selected);
        var i = _grid.Rows.Add(show, name, null, full);
        var cell = (DataGridViewComboBoxCell)_grid.Rows[i].Cells[2];
        cell.Items.AddRange(choices.Select(c => (object)c.Label).ToArray());
        cell.Value = choices.First(c => c.MicId == selected).Label;
        cell.Tag = choices;
        _grid.Rows[i].Tag = (id, suggested);
    }

    private void FillStandard(string? select = null)
    {
        select ??= (_standard.SelectedItem as StandardItem)?.Id;
        _standard.Items.Clear();
        _standard.Items.Add(new StandardItem(null, L.T("(none)", "(ingen)")));
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var (id, _) = ((string, string))row.Tag!;
            _standard.Items.Add(new StandardItem(id, row.Cells[1].Value as string ?? id));
        }
        // A disconnected default device that is not shown in the widget is missing from the list – but must not be forgotten
        if (select != null && _standard.Items.Cast<StandardItem>().All(i => i.Id != select))
        {
            var name = _names.TryGetValue(select, out var n) && !string.IsNullOrWhiteSpace(n) ? n : L.T("Default device", "Standardenhed");
            _standard.Items.Add(new StandardItem(select, name + L.T(" (not connected)", " (ikke tilsluttet)")));
        }
        _standard.SelectedItem = _standard.Items.Cast<StandardItem>().FirstOrDefault(i => i.Id == select) ?? _standard.Items[0];
    }

    private void MoveRow(int delta)
    {
        if (_grid.CurrentRow is not { } row) return;
        var to = row.Index + delta;
        if (to < 0 || to >= _grid.Rows.Count) return;
        _grid.Rows.RemoveAt(row.Index);
        _grid.Rows.Insert(to, row);
        _automatic = false;
        _grid.CurrentCell = row.Cells[1];
    }

    public override string? SaveTo(AppSettings s)
    {
        _grid.EndEdit();
        s.ShowAudio = _show.Checked;
        var shown = new List<string>();
        var names = new Dictionary<string, string>(_names);
        // Pairs for outputs that are not in the list right now are kept
        var pairs = new Dictionary<string, string>(_loaded?.AudioMicPairs ?? new());
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var (id, suggested) = ((string, string))row.Tag!;
            var micCell = row.Cells[2];
            var mic = (micCell.Tag as List<(string Label, string? MicId)>)?.FirstOrDefault(c => c.Label == micCell.Value as string).MicId;
            if (mic == null) pairs.Remove(id);
            else pairs[id] = mic;
            if (row.Cells[0].Value is true) shown.Add(id);
            var name = (row.Cells[1].Value as string ?? "").Trim();
            if (name.Length == 0 || name == suggested) names.Remove(id);
            else names[id] = name;
        }
        // An unchanged "all devices" stays automatic, so devices connected later are shown too
        s.AudioDeviceIds = _automatic ? null : shown.ToArray();
        s.AudioNames = names;
        s.AudioStandardId = (_standard.SelectedItem as StandardItem)?.Id;
        s.AudioBatteryColor = _batteryColor is { } c ? BatteryColorSetting.Format(c) : null;
        s.AudioBatteryWarn = _batteryWarn.Checked;
        s.AudioBatteryAlert = _batteryAlert.Checked;
        s.AudioBatteryLowLevel = (int)_batteryLow.Value;
        s.AudioBatteryCriticalLevel = (int)_batteryCritical.Value;
        s.AudioSwitchMic = _switchMic.Checked;
        s.AudioMicPairs = pairs;
        s.AudioShowMic = _showMic.Checked;
        s.AudioShowVolume = _showVolume.Checked;
        s.AudioVolumeStep = (int)_volumeStep.Value;
        return _batteryAlert.Checked && _batteryCritical.Value >= _batteryLow.Value
            ? L.T("The last battery message must come at a lower percentage than the first.", "Sidste batteribesked skal komme ved en lavere procent end den første.")
            : null;
    }
}
