using System.Text.Json.Serialization;

/// <summary>What makes a price rule apply.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum AutomationCondition
{
    /// <summary>The cheapest <see cref="PriceAutomation.Hours"/> hours between two times of day – not necessarily side by side.</summary>
    CheapestHours,
    PriceBelow,
    PriceAbove,
    /// <summary>The price is green in the widget.</summary>
    Cheap,
    /// <summary>The price is red in the widget.</summary>
    Expensive
}

/// <summary>
/// A rule that switches a Home Assistant entity by the electricity price, e.g. "turn on the water heater in the cheapest
/// 3 hours". Stored in the settings; the property names are keys in settings.json.
/// </summary>
internal sealed class PriceAutomation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string EntityId { get; set; } = "";
    public AutomationCondition Condition { get; set; } = AutomationCondition.CheapestHours;
    /// <summary>For <see cref="AutomationCondition.CheapestHours"/>: how many hours.</summary>
    public double Hours { get; set; } = 3;
    /// <summary>For <see cref="AutomationCondition.CheapestHours"/>: the hours are found between these times (0–24).
    /// Equal = the whole day; from after to = overnight, e.g. 18 to 7.</summary>
    public int FromHour { get; set; }
    public int ToHour { get; set; }
    /// <summary>For <see cref="AutomationCondition.PriceBelow"/> and <see cref="AutomationCondition.PriceAbove"/>, in the price unit per kWh.</summary>
    public double Threshold { get; set; }
    /// <summary>Turn the entity on while the rule applies (otherwise off).</summary>
    public bool TurnOn { get; set; } = true;
    /// <summary>Do the opposite when the rule stops applying.</summary>
    public bool Revert { get; set; } = true;

    /// <summary>Changes when anything that decides what the rule does changes, so an edited rule is applied again.</summary>
    [JsonIgnore]
    public string Signature => $"{Id}|{EntityId}|{Condition}|{Hours}|{FromHour}|{ToHour}|{Threshold}|{TurnOn}|{Revert}";

    public static PriceAutomation[] Templates() => new[]
    {
        new PriceAutomation { Name = L.T("Water heater in the cheapest hours", "Varmtvandsbeholder i de billigste timer"), Hours = 3 },
        new PriceAutomation { Name = L.T("Charge the car overnight", "Oplad elbilen om natten"), Hours = 4, FromHour = 18, ToHour = 7 },
        new PriceAutomation { Name = L.T("Off when power is expensive", "Sluk når strømmen er dyr"), Condition = AutomationCondition.Expensive, TurnOn = false },
    };
}

internal static class AutomationRules
{
    /// <summary>The stretch of time a <see cref="AutomationCondition.CheapestHours"/> rule picks its hours in, around <paramref name="t"/>.
    /// Null when <paramref name="t"/> is outside the rule's times of day.</summary>
    public static (DateTime From, DateTime To)? Range(PriceAutomation rule, DateTime t)
    {
        int from = Math.Clamp(rule.FromHour, 0, 24) % 24, to = Math.Clamp(rule.ToHour, 0, 24) % 24;
        var day = t.Date;
        var hour = t.TimeOfDay.TotalHours;
        if (from == to) return (day.AddHours(from > hour ? from - 24 : from), day.AddHours(from > hour ? from : from + 24));
        if (from < to) return hour >= from && hour < to ? (day.AddHours(from), day.AddHours(to)) : null;
        if (hour >= from) return (day.AddHours(from), day.AddDays(1).AddHours(to));
        if (hour < to) return (day.AddDays(-1).AddHours(from), day.AddHours(to));
        return null;
    }

    /// <summary>The periods a <see cref="AutomationCondition.CheapestHours"/> rule picks in the range around <paramref name="t"/>.</summary>
    public static List<PricePeriod> Chosen(PriceAutomation rule, IReadOnlyList<PricePeriod> periods, DateTime t) =>
        Range(rule, t) is { } range
            ? PriceWindows.CheapestSet(periods, range.From, range.To, TimeSpan.FromHours(Math.Clamp(rule.Hours, 0.25, 24)))
            : new List<PricePeriod>();

    /// <summary>Whether the rule applies at <paramref name="now"/>. Null when there is no price for now.</summary>
    public static bool? Applies(PriceAutomation rule, IReadOnlyList<PricePeriod> periods, IReadOnlyList<double> hourValues, DateTime now)
    {
        if (PriceWindows.At(periods, now) is not { } current) return null;
        return rule.Condition switch
        {
            AutomationCondition.CheapestHours => Chosen(rule, periods, now).Any(p => p.Start <= now && now < p.End),
            AutomationCondition.PriceBelow => current.Value < rule.Threshold,
            AutomationCondition.PriceAbove => current.Value > rule.Threshold,
            AutomationCondition.Cheap => ElectricityPriceService.Level(current.Value, hourValues) == PriceLevel.Cheap,
            AutomationCondition.Expensive => ElectricityPriceService.Level(current.Value, hourValues) == PriceLevel.Expensive,
            _ => false
        };
    }

    /// <summary>The coming stretches (from now on) where the rule applies, as far as the prices are known.</summary>
    public static List<PriceWindow> Upcoming(PriceAutomation rule, IReadOnlyList<PricePeriod> periods, IReadOnlyList<double> hourValues, DateTime now)
    {
        IEnumerable<PricePeriod> matching;
        if (rule.Condition == AutomationCondition.CheapestHours)
        {
            // The range around each period: today's and the next ranges, as far as prices are known
            // A range that isn't fully known yet shows its cheapest known hours
            var chosen = new Dictionary<DateTime, PricePeriod>();
            var ranges = new HashSet<DateTime>();
            foreach (var p in periods.Where(p => p.End > now))
            {
                if (Range(rule, p.Start) is not { } range || !ranges.Add(range.From)) continue;
                foreach (var c in PriceWindows.CheapestSet(periods, range.From, range.To, TimeSpan.FromHours(Math.Clamp(rule.Hours, 0.25, 24))))
                    chosen[c.Start] = c;
            }
            matching = chosen.Values.Where(p => p.End > now);
        }
        else
        {
            matching = periods.Where(p => p.End > now && rule.Condition switch
            {
                AutomationCondition.PriceBelow => p.Value < rule.Threshold,
                AutomationCondition.PriceAbove => p.Value > rule.Threshold,
                AutomationCondition.Cheap => ElectricityPriceService.Level(p.Value, hourValues) == PriceLevel.Cheap,
                AutomationCondition.Expensive => ElectricityPriceService.Level(p.Value, hourValues) == PriceLevel.Expensive,
                _ => false
            });
        }
        return PriceWindows.Merge(matching);
    }

    /// <summary>What to do with the entity now: true = on, false = off, null = nothing.</summary>
    /// <param name="applies">Whether the rule applies now.</param>
    /// <param name="previous">Whether it applied at the last check; null if the rule is new or was changed.</param>
    public static bool? Action(PriceAutomation rule, bool applies, bool? previous)
    {
        if (previous == applies) return null;
        // A new or edited rule leaves the entity alone until its time comes, instead of switching it at once
        if (previous == null && !applies) return null;
        if (applies) return rule.TurnOn;
        return rule.Revert ? !rule.TurnOn : null;
    }

    /// <summary>"in the cheapest 3 hours between 18 and 07" – what the rule does, in words.</summary>
    public static string Describe(PriceAutomation rule, PriceUnit unit)
    {
        var action = rule.TurnOn ? L.T("On", "Tænd") : L.T("Off", "Sluk");
        var hours = PriceAlerts.Hours((int)Math.Round(Math.Clamp(rule.Hours, 0.25, 24) * 60));
        var when = rule.Condition switch
        {
            AutomationCondition.CheapestHours => (rule.FromHour % 24 == rule.ToHour % 24
                ? L.T($"in the cheapest {hours} of the day", $"i døgnets billigste {hours}")
                : L.T($"in the cheapest {hours} between {rule.FromHour:00} and {rule.ToHour:00}", $"i de billigste {hours} mellem kl. {rule.FromHour:00} og {rule.ToHour:00}")),
            AutomationCondition.PriceBelow => L.T($"when the price is below {unit.Format(rule.Threshold, L.Culture)} {unit.PerKwh}", $"når prisen er under {unit.Format(rule.Threshold, L.Culture)} {unit.PerKwh}"),
            AutomationCondition.PriceAbove => L.T($"when the price is above {unit.Format(rule.Threshold, L.Culture)} {unit.PerKwh}", $"når prisen er over {unit.Format(rule.Threshold, L.Culture)} {unit.PerKwh}"),
            AutomationCondition.Cheap => L.T("when power is cheap", "når strømmen er billig"),
            _ => L.T("when power is expensive", "når strømmen er dyr")
        };
        return $"{action} {when}";
    }
}
