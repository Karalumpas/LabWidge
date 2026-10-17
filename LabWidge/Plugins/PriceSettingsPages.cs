using System.Drawing;
using System.Globalization;
using System.Net.Http;
using System.Windows.Forms;

// The electricity price's own settings pages, shown under "Electricity" in the settings menu:
// price alerts, appliances, Home Assistant rules and history.

/// <summary>The price alert switches – on their own page in the settings, and in the setup guide's notifications step.</summary>
internal sealed class PriceAlertControls
{
    private readonly CheckBox _cheap = Ui.Check(L.T("Before the cheapest stretch of the day starts", "Før dagens billigste periode starter"));
    private readonly NumericUpDown _cheapHours = new() { Width = 70, Minimum = 0.5m, Maximum = 8, Increment = 0.25m, DecimalPlaces = 2 };
    private readonly CheckBox _expensive = Ui.Check(L.T("Before power gets expensive", "Før strømmen bliver dyr"));
    private readonly NumericUpDown _lead = new() { Width = 70, Minimum = 5, Maximum = 240, Increment = 5 };
    private readonly CheckBox _quiet = Ui.Check(L.T("No price messages at night (23–07)", "Ingen prisbeskeder om natten (kl. 23–07)"));
    private readonly Control _lengthRow;

    public PriceAlertControls()
    {
        _lengthRow = Ui.Group(Ui.Row(L.T("Length of the stretch", "Periodens længde"), _cheapHours, Ui.Inline(L.T("hours", "timer"))));
        _cheap.CheckedChanged += (_, _) => _lengthRow.Enabled = _cheap.Checked;
    }

    public Control[] Controls(bool detailed) => detailed
        ? new Control[]
        {
            Ui.Section(L.T("Cheap power", "Billig strøm")), _cheap, _lengthRow,
            Ui.Help(L.T("The alert uses the market's quarter-hours, so a cheap stretch can start at e.g. 13:15.",
                        "Alarmen bruger markedets kvarterpriser, så en billig periode kan starte fx kl. 13:15.")),
            Ui.Section(L.T("Expensive power", "Dyr strøm")), _expensive,
            Ui.Help(L.T("Expensive means the most expensive third of the day's prices – red in the widget.",
                        "Dyr betyder den dyreste tredjedel af døgnets priser – rød i widgetten.")),
            Ui.Section(L.T("When", "Hvornår")),
            Ui.Row(L.T("Notify", "Send besked"), _lead, Ui.Inline(L.T("minutes before", "minutter før"))), _quiet
        }
        : new Control[]
        {
            Ui.Section(L.T("Price alerts", "Prisalarmer")), _cheap, _lengthRow, _expensive,
            Ui.Row(L.T("Notify", "Send besked"), _lead, Ui.Inline(L.T("minutes before", "minutter før"))), _quiet
        };

    public void LoadFrom(AppSettings s)
    {
        _cheap.Checked = s.AlertCheap;
        _cheapHours.Value = Math.Clamp(s.AlertCheapMinutes, 30, 480) / 60m;
        _lengthRow.Enabled = s.AlertCheap;
        _expensive.Checked = s.AlertExpensive;
        _lead.Value = Math.Clamp(s.AlertLeadMinutes, 5, 240);
        _quiet.Checked = s.AlertQuietNight;
    }

    public void SaveTo(AppSettings s)
    {
        s.AlertCheap = _cheap.Checked;
        s.AlertCheapMinutes = (int)Math.Round(_cheapHours.Value * 60);
        s.AlertExpensive = _expensive.Checked;
        s.AlertLeadMinutes = (int)_lead.Value;
        s.AlertQuietNight = _quiet.Checked;
    }
}

internal sealed class PriceAlertsPage : SettingsPage
{
    private readonly PriceAlertControls _alerts = new();
    public override string Title => L.T("Price alerts", "Prisalarmer");
    public override string Glyph => "";

    public PriceAlertsPage()
    {
        Add(Ui.Heading(Title),
            Ui.Help(L.T("Windows notifications that tell you when to use power – and when to wait.",
                        "Windows-notifikationer, der fortæller, hvornår du skal bruge strøm – og hvornår du skal vente.")));
        Add(_alerts.Controls(detailed: true));
    }

    public override void LoadFrom(AppSettings s) => _alerts.LoadFrom(s);
    public override string? SaveTo(AppSettings s)
    {
        _alerts.SaveTo(s);
        return null;
    }
}

// ---------------------------------------------------------------------------

/// <summary>Home Assistant entities to choose from in the price pages, fetched once per settings window.</summary>
internal static class HaEntityChoices
{
    internal sealed record Item(string Id, string Name)
    {
        public override string ToString() => Name.Length == 0 || Name == Id ? Id : $"{Name} – {Id}";
    }

    /// <summary>Domains that can be switched on and off.</summary>
    private static readonly string[] Domains = { "switch", "light", "input_boolean", "fan", "climate", "water_heater", "humidifier", "script", "automation", "siren" };
    private static readonly HttpClient Http = HttpClientFactory.Create(TimeSpan.FromSeconds(15));
    private static (string Url, DateTime At, Task<IReadOnlyList<Item>> Items)? _cache;

    public static Task<IReadOnlyList<Item>> LoadAsync(AppSettings s)
    {
        var url = s.HomeAssistantUrl ?? "";
        if (_cache is { } c && c.Url == url && DateTime.Now - c.At < TimeSpan.FromMinutes(5) && !c.Items.IsFaulted) return c.Items;
        var task = FetchAsync(url);
        _cache = (url, DateTime.Now, task);
        return task;
    }

    private static async Task<IReadOnlyList<Item>> FetchAsync(string url)
    {
        var token = CredentialStore.ReadHomeAssistantToken();
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token)) return Array.Empty<Item>();
        var all = await HomeAssistantService.FetchAllAsync(Http, url, token);
        return all.Where(e => Domains.Contains(e.Domain)).OrderBy(e => e.Domain).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                  .Select(e => new Item(e.EntityId, e.Name)).ToList();
    }

    public static ComboBox CreateCombo() => new()
    {
        Width = Ui.FieldWidth, DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems
    };

    public static void Fill(ComboBox combo, IReadOnlyList<Item> items)
    {
        var current = Read(combo);
        combo.BeginUpdate();
        combo.Items.Clear();
        combo.Items.AddRange(items.Cast<object>().ToArray());
        combo.EndUpdate();
        Show(combo, current);
    }

    public static void Show(ComboBox combo, string? entityId)
    {
        var item = combo.Items.OfType<Item>().FirstOrDefault(i => i.Id == entityId);
        if (item != null) combo.SelectedItem = item;
        else combo.Text = entityId ?? "";
    }

    /// <summary>The chosen entity id: the item, or what was typed ("Name – light.kitchen" or just "light.kitchen").</summary>
    public static string? Read(ComboBox combo)
    {
        if (combo.SelectedItem is Item item && combo.Text == item.ToString()) return item.Id;
        var text = combo.Text.Trim();
        var dash = text.LastIndexOf(" – ", StringComparison.Ordinal);
        if (dash >= 0) text = text[(dash + 3)..].Trim();
        return text.Length == 0 ? null : text;
    }

    public static bool LooksLikeEntity(string id) => id.Contains('.') && !id.Any(char.IsWhiteSpace);
}

/// <summary>A row of buttons, starting at the left edge.</summary>
internal static class ButtonRow
{
    public static FlowLayoutPanel Of(params Control[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(Ui.ContentWidth, 0), Margin = new Padding(0, 4, 0, 2) };
        foreach (var button in buttons)
        {
            button.Margin = new Padding(0, 0, 8, 6);
            row.Controls.Add(button);
        }
        return row;
    }
}

// ---------------------------------------------------------------------------

internal sealed class AppliancesPage : SettingsPage
{
    private readonly CheckBox _reminders = Ui.Check(L.T("Remind me when a planned run should start", "Mind mig om det, når en planlagt kørsel skal starte"));
    private readonly List<ApplianceEditor> _editors = new();
    private readonly Card _addCard;
    private AppSettings _settings = new();
    private IReadOnlyList<HaEntityChoices.Item> _entities = Array.Empty<HaEntityChoices.Item>();

    public override string Title => L.T("Appliances", "Apparater");
    public override string Glyph => "";

    public AppliancesPage()
    {
        var add = new Button { Text = L.T("Add appliance", "Tilføj apparat"), AutoSize = true, Tag = "accent" };
        add.Click += (_, _) => AddEditor(new Appliance { Name = L.T("New appliance", "Nyt apparat") }, focus: true);
        var suggestions = Appliance.Suggestions().Select(a =>
        {
            var b = new Button { Text = "+ " + a.Name, AutoSize = true };
            b.Click += (_, _) => AddEditor(new Appliance
            {
                Name = a.Name, DurationMinutes = a.DurationMinutes, EnergyKwh = a.EnergyKwh, FinishBy = a.FinishBy
            }, focus: true);
            return (Control)b;
        }).ToArray();

        Add(Ui.Heading(Title),
            Ui.Help(L.T("Tell LabWidge how long your appliances run, and it finds the cheapest time to start them – also across midnight. " +
                        "Click an appliance in the widget to plan a run: LabWidge reminds you when to start it, or switches it on through Home Assistant.",
                        "Fortæl LabWidge, hvor længe dine apparater kører, så finder den det billigste tidspunkt at starte dem – også hen over midnat. " +
                        "Klik på et apparat i widgetten for at planlægge en kørsel: LabWidge minder dig om at starte den, eller tænder den via Home Assistant.")),
            Ui.Section(L.T("Reminders", "Påmindelser")), _reminders);
        Add(Ui.Section(L.T("Add an appliance", "Tilføj et apparat")),
            Ui.Help(L.T("Start from a suggestion and adjust it to your own appliance.", "Start med et forslag, og tilpas det til dit eget apparat.")),
            ButtonRow.Of(suggestions.Prepend(add).ToArray()));
        _addCard = (Card)Body.Controls[^1];
    }

    private void AddEditor(Appliance a, bool focus = false)
    {
        var editor = new ApplianceEditor(a, _settings, () => Remove(a.Id));
        HaEntityChoices.Fill(editor.Entity, _entities);
        HaEntityChoices.Show(editor.Entity, a.HomeAssistantEntity);
        _editors.Add(editor);
        Body.Controls.Add(editor);
        Body.Controls.SetChildIndex(editor, Body.Controls.IndexOf(_addCard));
        SettingsTheme.Apply(this, Ui.Palette);
        if (focus)
        {
            editor.FocusName();
            ScrollControlIntoView(editor);
        }
    }

    private void Remove(string id)
    {
        if (_editors.FirstOrDefault(e => e.Id == id) is not { } editor) return;
        _editors.Remove(editor);
        Body.Controls.Remove(editor);
        editor.Dispose();
    }

    public override async Task OnFirstShownAsync()
    {
        if (!_settings.HasHomeAssistantConnection) return;
        try
        {
            _entities = await HaEntityChoices.LoadAsync(_settings);
            foreach (var editor in _editors) HaEntityChoices.Fill(editor.Entity, _entities);
        }
        catch (Exception ex)
        {
            Logger.Error($"Home Assistant entities could not be fetched for the appliances: {ex.Message}");
        }
    }

    public override void LoadFrom(AppSettings s)
    {
        _settings = s;
        _reminders.Checked = s.ApplianceReminders;
        foreach (var editor in _editors.ToList()) Remove(editor.Id);
        foreach (var a in s.Appliances) AddEditor(a);
    }

    public override string? SaveTo(AppSettings s)
    {
        var list = new List<Appliance>();
        foreach (var editor in _editors)
        {
            var (appliance, error) = editor.Read();
            if (error != null) return error;
            list.Add(appliance!);
        }
        s.ApplianceReminders = _reminders.Checked;
        s.Appliances = list;
        return null;
    }
}

/// <summary>One appliance in the settings, in its own card.</summary>
internal sealed class ApplianceEditor : Card
{
    private readonly Label _title = Ui.Section("");
    private readonly TextBox _name = new() { Width = Ui.FieldWidth };
    private readonly NumericUpDown _hours = new() { Width = 56, Minimum = 0, Maximum = 23 };
    private readonly NumericUpDown _minutes = new() { Width = 56, Minimum = 0, Maximum = 55, Increment = 5 };
    private readonly NumericUpDown _kwh = new() { Width = 80, Minimum = 0, Maximum = 200, DecimalPlaces = 1, Increment = 0.1m };
    private readonly CheckBox _finish = Ui.Check(L.T("Must be finished by a certain time", "Skal være færdig til et bestemt tidspunkt"));
    private readonly ComboBox _finishBy = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _widget = Ui.Check(L.T("Show in the widget", "Vis i widgetten"));
    public readonly ComboBox Entity = HaEntityChoices.CreateCombo();
    private readonly CheckBox _turnOff = Ui.Check(L.T("Switch it off again when the run is finished", "Sluk igen, når kørslen er færdig"));
    private readonly Label _preview = Ui.Help("");
    private readonly Control _finishRow, _entityGroup;
    private readonly AppSettings _settings;

    public string Id { get; }

    public ApplianceEditor(Appliance a, AppSettings settings, Action remove)
    {
        Id = a.Id;
        _settings = settings;
        for (var m = 0; m < 24 * 60; m += 30) _finishBy.Items.Add($"{m / 60:00}:{m % 60:00}");
        _finishRow = Ui.Group(Ui.Row(L.T("Finished by", "Færdig senest kl."), _finishBy));
        var hasHa = settings.HasHomeAssistantConnection;
        _entityGroup = Ui.Stack(
            Ui.Row(L.T("Home Assistant", "Home Assistant"), Entity),
            Ui.Help(L.T("Optional: a smart plug or switch that LabWidge switches on when a planned run starts. Leave it empty for a reminder only.",
                        "Valgfrit: et smart stik eller en kontakt, som LabWidge tænder, når en planlagt kørsel starter. Lad feltet stå tomt for kun en påmindelse.")),
            _turnOff);
        _entityGroup.Visible = hasHa || !string.IsNullOrWhiteSpace(a.HomeAssistantEntity);

        var remove_ = new Button { Text = L.T("Remove", "Fjern"), AutoSize = true };
        remove_.Click += (_, _) => remove();

        Controls.AddRange(new Control[]
        {
            _title,
            Ui.Row(L.T("Name", "Navn"), _name),
            Ui.Row(L.T("Run time", "Varighed"), _hours, Ui.Inline(L.T("h", "t")), _minutes, Ui.Inline("min")),
            Ui.Row(L.T("Energy per run", "Forbrug pr. kørsel"), _kwh, Ui.Inline(L.T("kWh  (0 = unknown)", "kWh  (0 = ukendt)"))),
            _finish, _finishRow, _widget, _entityGroup, _preview,
            ButtonRow.Of(remove_)
        });

        _name.Text = a.Name;
        _title.Text = a.Name;
        var duration = Math.Clamp(a.DurationMinutes, 5, 23 * 60 + 55);
        _hours.Value = duration / 60;
        _minutes.Value = duration % 60 / 5 * 5;
        _kwh.Value = (decimal)Math.Clamp(a.EnergyKwh, 0, 200);
        _finish.Checked = a.FinishBy != null;
        _finishBy.SelectedIndex = Math.Clamp((a.FinishBy ?? 7 * 60) / 30, 0, 47);
        _finishRow.Enabled = _finish.Checked;
        _widget.Checked = a.ShowInWidget;
        _turnOff.Checked = a.TurnOffAfter;

        _name.TextChanged += (_, _) => { _title.Text = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : L.T("Appliance", "Apparat"); UpdatePreview(); };
        _finish.CheckedChanged += (_, _) => { _finishRow.Enabled = _finish.Checked; UpdatePreview(); };
        foreach (var n in new[] { _hours, _minutes, _kwh }) n.ValueChanged += (_, _) => UpdatePreview();
        _finishBy.SelectedIndexChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    public void FocusName()
    {
        _name.Focus();
        _name.SelectAll();
    }

    private Appliance Current() => new()
    {
        Id = Id,
        Name = _name.Text.Trim(),
        DurationMinutes = Math.Max(5, (int)_hours.Value * 60 + (int)_minutes.Value),
        EnergyKwh = (double)_kwh.Value,
        FinishBy = _finish.Checked ? _finishBy.SelectedIndex * 30 : null,
        ShowInWidget = _widget.Checked,
        HomeAssistantEntity = HaEntityChoices.Read(Entity),
        TurnOffAfter = _turnOff.Checked
    };

    /// <summary>The cheapest start with today's prices, so the user sees what the settings mean.</summary>
    private void UpdatePreview()
    {
        var el = ElectricityPriceService.Shared;
        var a = Current();
        if (el == null || el.Prices.Count == 0 || _settings.PriceCountry == null)
        {
            _preview.Text = "";
            return;
        }
        var now = DateTime.Now;
        var unit = _settings.PriceUnit;
        if (AppliancePlanner.Plan(a, el.ConsumerPeriods(_settings), now) is not { } plan)
        {
            _preview.Text = L.T("The known prices don't cover a whole run before the deadline yet.", "De kendte priser dækker endnu ikke en hel kørsel før tidsfristen.");
            _preview.ForeColor = Ui.Muted;
            return;
        }
        var cost = plan.Cost(a, unit) is { } c ? L.T($" and costs about {Money.Format(c, unit)}", $" og koster ca. {Money.Format(c, unit)}") : "";
        var saving = plan.Saving(a, unit) is > 0.005 and var sv ? L.T($" – {Money.Format(sv, unit)} less than starting now", $" – {Money.Format(sv, unit)} mindre end at starte nu") : "";
        _preview.Text = (plan.StartsNow(now) ? L.T("With the current prices the cheapest time is right now", "Med de nuværende priser er det billigst at starte nu")
                                             : L.T("With the current prices it is cheapest to start ", "Med de nuværende priser er det billigst at starte ") + PriceWindows.Describe(plan.Best, now))
                        + cost + saving + ".";
        _preview.ForeColor = Ui.Ok;
    }

    public (Appliance? Appliance, string? Error) Read()
    {
        var a = Current();
        if (a.Name.Length == 0) return (null, L.T("Give each appliance a name.", "Giv hvert apparat et navn."));
        if (a.HomeAssistantEntity is { } id && !HaEntityChoices.LooksLikeEntity(id))
            return (null, L.T($"\"{id}\" is not a Home Assistant entity – choose one from the list, e.g. switch.dishwasher.",
                              $"\"{id}\" er ikke en Home Assistant-entitet – vælg en fra listen, fx switch.opvaskemaskine."));
        return (a, null);
    }
}

// ---------------------------------------------------------------------------

internal sealed class AutomationsPage : SettingsPage
{
    private readonly List<RuleEditor> _editors = new();
    private readonly Card _addCard;
    private readonly Label _haStatus = Ui.Help("");
    private readonly LinkLabel _haLink = new() { Text = L.T("Set up Home Assistant", "Opsæt Home Assistant"), AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
    private readonly Control _haCard;
    private AppSettings _settings = new();
    private IReadOnlyList<HaEntityChoices.Item> _entities = Array.Empty<HaEntityChoices.Item>();

    public override string Title => L.T("Automation", "Automatik");
    public override string Glyph => "";

    public AutomationsPage()
    {
        var add = new Button { Text = L.T("New rule", "Ny regel"), AutoSize = true, Tag = "accent" };
        add.Click += (_, _) => AddEditor(new PriceAutomation { Name = L.T("New rule", "Ny regel") }, focus: true);
        var templates = PriceAutomation.Templates().Select(t =>
        {
            var b = new Button { Text = "+ " + t.Name, AutoSize = true };
            b.Click += (_, _) => AddEditor(new PriceAutomation
            {
                Name = t.Name, Condition = t.Condition, Hours = t.Hours, FromHour = t.FromHour, ToHour = t.ToHour, TurnOn = t.TurnOn, Revert = t.Revert
            }, focus: true);
            return (Control)b;
        }).ToArray();
        _haLink.LinkClicked += (_, _) => Navigate("ha");

        Add(Ui.Heading(Title),
            Ui.Help(L.T("Let the electricity price switch things in Home Assistant – e.g. the water heater in the cheapest hours, or the car charger overnight. " +
                        "LabWidge runs the rules, so they work while the PC is on and LabWidge is running.",
                        "Lad elprisen styre ting i Home Assistant – fx varmtvandsbeholderen i de billigste timer eller elbilladeren om natten. " +
                        "Det er LabWidge, der kører reglerne, så de virker, mens pc'en er tændt og LabWidge kører.")),
            Ui.Section(L.T("Connection", "Forbindelse")), _haStatus, _haLink);
        _haCard = Body.Controls[^1];
        Add(Ui.Section(L.T("Add a rule", "Tilføj en regel")),
            Ui.Help(L.T("Start from an example and choose the entity it should switch.", "Start med et eksempel, og vælg den entitet, den skal styre.")),
            ButtonRow.Of(templates.Prepend(add).ToArray()));
        _addCard = (Card)Body.Controls[^1];
    }

    private void AddEditor(PriceAutomation rule, bool focus = false)
    {
        var editor = new RuleEditor(rule, _settings, () => Remove(rule.Id));
        HaEntityChoices.Fill(editor.Entity, _entities);
        HaEntityChoices.Show(editor.Entity, rule.EntityId);
        _editors.Add(editor);
        Body.Controls.Add(editor);
        Body.Controls.SetChildIndex(editor, Body.Controls.IndexOf(_addCard));
        SettingsTheme.Apply(this, Ui.Palette);
        if (focus)
        {
            editor.FocusName();
            ScrollControlIntoView(editor);
        }
    }

    private void Remove(string id)
    {
        if (_editors.FirstOrDefault(e => e.Id == id) is not { } editor) return;
        _editors.Remove(editor);
        Body.Controls.Remove(editor);
        editor.Dispose();
    }

    private void ShowConnection()
    {
        var connected = _settings.HasHomeAssistantConnection;
        _haStatus.Text = connected
            ? L.T($"✓ Connected to {_settings.HomeAssistantUrl}", $"✓ Forbundet til {_settings.HomeAssistantUrl}")
            : L.T("Home Assistant isn't set up. Turn on the Home Assistant plugin and connect it first.",
                  "Home Assistant er ikke sat op. Slå Home Assistant-pluginnet til, og forbind det først.");
        _haStatus.ForeColor = connected ? Ui.Ok : Ui.Warn;
        _haLink.Text = connected ? L.T("Home Assistant settings", "Home Assistant-indstillinger") : L.T("Set up Home Assistant", "Opsæt Home Assistant");
        _haCard.Visible = true;
    }

    public override async Task OnFirstShownAsync()
    {
        ShowConnection();
        if (!_settings.HasHomeAssistantConnection) return;
        try
        {
            _entities = await HaEntityChoices.LoadAsync(_settings);
            foreach (var editor in _editors) HaEntityChoices.Fill(editor.Entity, _entities);
            if (_entities.Count == 0)
            {
                _haStatus.Text = L.T("Home Assistant has no entities that can be switched on and off.", "Home Assistant har ingen entiteter, der kan tændes og slukkes.");
                _haStatus.ForeColor = Ui.Warn;
            }
        }
        catch (Exception ex)
        {
            _haStatus.Text = L.T("Could not fetch the entities: ", "Kunne ikke hente entiteterne: ") + HomeAssistantService.Describe(ex);
            _haStatus.ForeColor = Ui.Error;
        }
    }

    public override void LoadFrom(AppSettings s)
    {
        _settings = s;
        ShowConnection();
        foreach (var editor in _editors.ToList()) Remove(editor.Id);
        foreach (var rule in s.PriceAutomations) AddEditor(rule);
    }

    public override string? SaveTo(AppSettings s)
    {
        var list = new List<PriceAutomation>();
        foreach (var editor in _editors)
        {
            var (rule, error) = editor.Read();
            if (error != null) return error;
            list.Add(rule!);
        }
        s.PriceAutomations = list;
        return null;
    }
}

/// <summary>One price rule in the settings, in its own card.</summary>
internal sealed class RuleEditor : Card
{
    private static readonly HttpClient Http = HttpClientFactory.Create(TimeSpan.FromSeconds(10));
    private readonly Label _title = Ui.Section("");
    private readonly CheckBox _enabled = Ui.Check(L.T("The rule is active", "Reglen er aktiv"));
    private readonly TextBox _name = new() { Width = Ui.FieldWidth };
    public readonly ComboBox Entity = HaEntityChoices.CreateCombo();
    private readonly ComboBox _condition = new() { Width = Ui.FieldWidth, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _hours = new() { Width = 70, Minimum = 0.25m, Maximum = 24, Increment = 0.25m, DecimalPlaces = 2 };
    private readonly ComboBox _from = new() { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _to = new() { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _threshold = new() { Width = 90, Minimum = -1000, Maximum = 10000, DecimalPlaces = 1, Increment = 1 };
    private readonly ComboBox _action = new() { Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _revert = Ui.Check(L.T("Do the opposite when the rule stops applying", "Gør det modsatte, når reglen ikke længere gælder"));
    private readonly Label _preview = Ui.Help("");
    private readonly Control _hoursRow, _rangeRow, _rangeHelp, _thresholdRow;
    private readonly AppSettings _settings;

    public string Id { get; }

    public RuleEditor(PriceAutomation rule, AppSettings settings, Action remove)
    {
        Id = rule.Id;
        _settings = settings;
        _condition.Items.AddRange(new object[]
        {
            L.T("In the cheapest hours", "I de billigste timer"),
            L.T("When the price is below", "Når prisen er under"),
            L.T("When the price is above", "Når prisen er over"),
            L.T("When power is cheap (green)", "Når strømmen er billig (grøn)"),
            L.T("When power is expensive (red)", "Når strømmen er dyr (rød)")
        });
        for (var h = 0; h <= 24; h++)
        {
            _from.Items.Add($"{h:00}:00");
            _to.Items.Add($"{h:00}:00");
        }
        _action.Items.AddRange(new object[] { L.T("Switch on", "Tænd"), L.T("Switch off", "Sluk") });

        _hoursRow = Ui.Row(L.T("How many hours", "Antal timer"), _hours, Ui.Inline(L.T("hours", "timer")));
        _rangeRow = Ui.Row(L.T("Between", "Mellem kl."), _from, Ui.Inline(L.T("and", "og")), _to);
        _rangeHelp = Ui.Help(L.T("The hours don't have to be in a row. The same time twice means the whole day; e.g. 18:00 and 07:00 means overnight.",
                                 "Timerne behøver ikke ligge i træk. Samme tidspunkt to gange betyder hele døgnet; fx 18:00 og 07:00 betyder hen over natten."));
        _thresholdRow = Ui.Row(L.T("Price", "Pris"), _threshold, Ui.Inline(settings.PriceUnit.PerKwh));

        var test = new Button { Text = L.T("Test now", "Test nu"), AutoSize = true };
        test.Click += async (_, _) => await TestAsync();
        var remove_ = new Button { Text = L.T("Remove", "Fjern"), AutoSize = true };
        remove_.Click += (_, _) => remove();

        Controls.AddRange(new Control[]
        {
            _title, _enabled,
            Ui.Row(L.T("Name", "Navn"), _name),
            Ui.Row(L.T("Entity", "Entitet"), Entity),
            Ui.Row(L.T("When", "Hvornår"), _condition),
            _hoursRow, _rangeRow, _rangeHelp, _thresholdRow,
            Ui.Row(L.T("Then", "Så"), _action),
            _revert, _preview,
            ButtonRow.Of(test, remove_)
        });

        _name.Text = _title.Text = rule.Name;
        _enabled.Checked = rule.Enabled;
        _condition.SelectedIndex = (int)rule.Condition;
        _hours.Value = (decimal)Math.Clamp(rule.Hours, 0.25, 24);
        _from.SelectedIndex = Math.Clamp(rule.FromHour, 0, 24);
        _to.SelectedIndex = Math.Clamp(rule.ToHour, 0, 24);
        _threshold.Value = (decimal)Math.Clamp(rule.Threshold, -1000, 10000);
        _action.SelectedIndex = rule.TurnOn ? 0 : 1;
        _revert.Checked = rule.Revert;
        ShowCondition();

        _name.TextChanged += (_, _) => _title.Text = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : L.T("Rule", "Regel");
        _condition.SelectedIndexChanged += (_, _) => { ShowCondition(); UpdatePreview(); };
        foreach (var c in new Control[] { _hours, _threshold }) ((NumericUpDown)c).ValueChanged += (_, _) => UpdatePreview();
        foreach (var c in new[] { _from, _to, _action }) c.SelectedIndexChanged += (_, _) => UpdatePreview();
        _revert.CheckedChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    public void FocusName()
    {
        _name.Focus();
        _name.SelectAll();
    }

    private void ShowCondition()
    {
        var c = (AutomationCondition)Math.Max(0, _condition.SelectedIndex);
        _hoursRow.Visible = _rangeRow.Visible = _rangeHelp.Visible = c == AutomationCondition.CheapestHours;
        _thresholdRow.Visible = c is AutomationCondition.PriceBelow or AutomationCondition.PriceAbove;
    }

    private PriceAutomation Current() => new()
    {
        Id = Id,
        Name = _name.Text.Trim(),
        Enabled = _enabled.Checked,
        EntityId = HaEntityChoices.Read(Entity) ?? "",
        Condition = (AutomationCondition)Math.Max(0, _condition.SelectedIndex),
        Hours = (double)_hours.Value,
        FromHour = Math.Max(0, _from.SelectedIndex),
        ToHour = Math.Max(0, _to.SelectedIndex),
        Threshold = (double)_threshold.Value,
        TurnOn = _action.SelectedIndex != 1,
        Revert = _revert.Checked
    };

    /// <summary>When the rule would apply with the prices known now.</summary>
    private void UpdatePreview()
    {
        var el = ElectricityPriceService.Shared;
        if (el == null || el.Prices.Count == 0 || _settings.PriceCountry == null)
        {
            _preview.Text = "";
            return;
        }
        var rule = Current();
        var now = DateTime.Now;
        var periods = el.ConsumerPeriods(_settings);
        var values = el.HourlyConsumer(_settings).Select(h => h.Value).ToList();
        var upcoming = AutomationRules.Upcoming(rule, periods, values, now).Take(4).ToList();
        var verb = rule.TurnOn ? L.T("On", "Tændt") : L.T("Off", "Slukket");
        _preview.Text = upcoming.Count == 0
            ? L.T("With the known prices the rule doesn't apply before they run out.", "Med de kendte priser gælder reglen ikke, før de slipper op.")
            : $"{verb} " + string.Join(", ", upcoming.Select(u => PriceWindows.Describe(u, now))) + ".";
        _preview.ForeColor = upcoming.Count == 0 ? Ui.Muted : Ui.Ok;
    }

    private async Task TestAsync()
    {
        var rule = Current();
        if (!_settings.HasHomeAssistantConnection || !HaEntityChoices.LooksLikeEntity(rule.EntityId))
        {
            _preview.Text = L.T("Choose an entity and connect Home Assistant first.", "Vælg en entitet, og forbind Home Assistant først.");
            _preview.ForeColor = Ui.Warn;
            return;
        }
        try
        {
            await new HomeAssistantService(Http).SetStateAsync(rule.EntityId, rule.TurnOn, _settings);
            _preview.Text = rule.TurnOn ? L.T($"✓ {rule.EntityId} was switched on.", $"✓ {rule.EntityId} blev tændt.")
                                        : L.T($"✓ {rule.EntityId} was switched off.", $"✓ {rule.EntityId} blev slukket.");
            _preview.ForeColor = Ui.Ok;
        }
        catch (Exception ex)
        {
            _preview.Text = L.T("The test failed: ", "Testen mislykkedes: ") + HomeAssistantService.Describe(ex);
            _preview.ForeColor = Ui.Error;
        }
    }

    public (PriceAutomation? Rule, string? Error) Read()
    {
        var rule = Current();
        if (rule.Name.Length == 0) rule.Name = rule.EntityId;
        if (rule.EntityId.Length == 0)
            return (null, L.T($"Choose the entity the rule \"{rule.Name}\" should switch – or remove the rule.",
                              $"Vælg den entitet, reglen \"{rule.Name}\" skal styre – eller fjern reglen."));
        if (!HaEntityChoices.LooksLikeEntity(rule.EntityId))
            return (null, L.T($"\"{rule.EntityId}\" is not a Home Assistant entity – choose one from the list.",
                              $"\"{rule.EntityId}\" er ikke en Home Assistant-entitet – vælg en fra listen."));
        return (rule, null);
    }
}

// ---------------------------------------------------------------------------

internal sealed class PriceHistoryPage : SettingsPage
{
    private static readonly HttpClient Http = HttpClientFactory.Create(TimeSpan.FromSeconds(30));
    private readonly CheckBox _enabled = Ui.Check(L.T("Keep the electricity prices on this PC", "Gem elpriserne på denne pc"));
    private readonly NumericUpDown _days = new() { Width = 80, Minimum = 30, Maximum = 2000, Increment = 30 };
    private readonly Label _summary = Ui.Help("");
    private readonly Label _status = Ui.Help("");
    private readonly Button _backfill = new() { Text = L.T("Fetch the last 30 days", "Hent de seneste 30 dage"), AutoSize = true };
    private readonly Button _export = new() { Text = L.T("Export as CSV …", "Eksportér som CSV …"), AutoSize = true };
    private readonly Button _clear = new() { Text = L.T("Delete history …", "Slet historik …"), AutoSize = true };
    private readonly Control _keepRow;
    private AppSettings _settings = new();
    private CancellationTokenSource? _fetch;

    public override string Title => L.T("History", "Historik");
    public override string Glyph => "";

    public PriceHistoryPage()
    {
        _keepRow = Ui.Group(Ui.Row(L.T("Keep for", "Gem i"), _days, Ui.Inline(L.T("days", "dage"))));
        _enabled.CheckedChanged += (_, _) => _keepRow.Enabled = _enabled.Checked;
        _backfill.Click += async (_, _) => await BackfillAsync();
        _export.Click += (_, _) => Export();
        _clear.Click += (_, _) =>
        {
            if (MessageBox.Show(FindForm(), L.T("Delete all saved prices and savings?", "Slet alle gemte priser og besparelser?"), Title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            PriceHistory.Clear();
            ShowSummary();
        };
        Disposed += (_, _) => _fetch?.Cancel();

        Add(Ui.Heading(Title),
            Ui.Help(L.T("LabWidge keeps each day's prices, so the electricity window can show the last 30 days, monthly averages and " +
                        "what your planned runs saved. Everything stays on this PC.",
                        "LabWidge gemmer hver dags priser, så elpris-vinduet kan vise de seneste 30 dage, månedens gennemsnit og " +
                        "hvad dine planlagte kørsler har sparet. Alt bliver på denne pc.")),
            Ui.Section(L.T("Price history", "Prishistorik")), _enabled, _keepRow, _summary,
            Ui.Section(L.T("Fill in and export", "Udfyld og eksportér")),
            Ui.Help(L.T("The history starts the day you install LabWidge. Fetch the last 30 days from the price source to get started at once; " +
                        "the price is calculated with your current settings.",
                        "Historikken starter den dag, du installerer LabWidge. Hent de seneste 30 dage fra priskilden for at komme i gang med det samme; " +
                        "prisen beregnes med dine nuværende indstillinger.")),
            ButtonRow.Of(_backfill, _export, _clear), _status,
            Ui.Section(L.T("Savings", "Besparelser")),
            Ui.Help(L.T("When a planned run of an appliance with a known energy use has finished, LabWidge notes what it cost and how much less " +
                        "it was than starting the appliance when you planned it.",
                        "Når en planlagt kørsel af et apparat med kendt forbrug er færdig, noterer LabWidge, hvad den kostede, og hvor meget mindre " +
                        "det var end at starte apparatet, da du planlagde den.")));
    }

    private string AreaKey => _settings.PriceCountry is { } c ? $"{c.Code}:{c.AreaOrDefault(_settings.PriceArea).Code}" : "";

    private void ShowSummary()
    {
        var days = PriceHistory.DaysFor(AreaKey);
        var unit = _settings.PriceUnit;
        var saved = PriceHistory.Data.Savings.Where(s => s.Currency == unit.Currency).ToList();
        _summary.Text = days.Count == 0
            ? L.T("No prices saved yet for this price area.", "Endnu ingen gemte priser for dette prisområde.")
            : L.T($"{days.Count} days saved, {days[0].Date:d MMM yyyy} – {days[^1].Date:d MMM yyyy}.", $"{days.Count} dage gemt, {days[0].Date:d. MMM yyyy} – {days[^1].Date:d. MMM yyyy}.")
              + (saved.Count > 0 ? L.T($" {saved.Count} planned runs saved {Money.Format(saved.Sum(s => s.Saved), unit)} in all.",
                                       $" {saved.Count} planlagte kørsler har sparet {Money.Format(saved.Sum(s => s.Saved), unit)} i alt.") : "");
        _export.Enabled = days.Count > 0;
    }

    private async Task BackfillAsync()
    {
        var el = ElectricityPriceService.Shared;
        if (el == null || _settings.PriceCountry == null) return;
        if (_fetch != null)
        {
            _fetch.Cancel();
            return;
        }
        _fetch = new CancellationTokenSource();
        _backfill.Text = L.T("Stop", "Stop");
        _status.ForeColor = Ui.Muted;
        try
        {
            var added = await PriceHistory.BackfillAsync(Http, el, _settings, 30, new Progress<string>(t => _status.Text = t), _fetch.Token);
            _status.Text = added == 0 ? L.T("✓ The last 30 days were already saved.", "✓ De seneste 30 dage var allerede gemt.")
                                      : L.T($"✓ {added} days fetched.", $"✓ {added} dage hentet.");
            _status.ForeColor = Ui.Ok;
        }
        catch (OperationCanceledException)
        {
            _status.Text = L.T("Stopped.", "Stoppet.");
        }
        catch (Exception ex)
        {
            _status.Text = L.T("Could not fetch the prices: ", "Kunne ikke hente priserne: ") + ex.Message;
            _status.ForeColor = Ui.Error;
            Logger.Error($"Fetching price history failed: {ex.Message}");
        }
        finally
        {
            _fetch.Dispose();
            _fetch = null;
            if (!IsDisposed)
            {
                _backfill.Text = L.T("Fetch the last 30 days", "Hent de seneste 30 dage");
                ShowSummary();
            }
        }
    }

    private void Export()
    {
        using var dialog = new SaveFileDialog
        {
            Title = L.T("Export the price history", "Eksportér prishistorikken"),
            Filter = "CSV|*.csv",
            FileName = $"labwidge-{AreaKey.Replace(':', '-').ToLowerInvariant()}-{DateTime.Today:yyyy-MM-dd}.csv"
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, PriceHistory.ToCsv(AreaKey));
            _status.Text = L.T($"✓ Saved as {Path.GetFileName(dialog.FileName)}", $"✓ Gemt som {Path.GetFileName(dialog.FileName)}");
            _status.ForeColor = Ui.Ok;
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            _status.ForeColor = Ui.Error;
        }
    }

    public override void LoadFrom(AppSettings s)
    {
        _settings = s;
        _enabled.Checked = s.PriceHistoryEnabled;
        _keepRow.Enabled = s.PriceHistoryEnabled;
        _days.Value = Math.Clamp(s.PriceHistoryDays, 30, 2000);
        ShowSummary();
    }

    public override string? SaveTo(AppSettings s)
    {
        s.PriceHistoryEnabled = _enabled.Checked;
        s.PriceHistoryDays = (int)_days.Value;
        return null;
    }
}
