using System.Globalization;

/// <summary>
/// An appliance the user wants to run when power is cheapest, e.g. the dishwasher. Stored in the settings;
/// the property names are keys in settings.json.
/// </summary>
internal sealed class Appliance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>How long a run takes.</summary>
    public int DurationMinutes { get; set; } = 120;
    /// <summary>Energy used by one run; 0 = unknown, so no cost is shown.</summary>
    public double EnergyKwh { get; set; }
    /// <summary>Must be finished by this time of day (minutes after midnight); null = any time within the known prices.</summary>
    public int? FinishBy { get; set; }
    public bool ShowInWidget { get; set; } = true;
    /// <summary>A Home Assistant entity (e.g. a smart plug) switched on when a planned run starts. Null = a reminder only.</summary>
    public string? HomeAssistantEntity { get; set; }
    /// <summary>Switch the entity off again when the run should be finished.</summary>
    public bool TurnOffAfter { get; set; }

    public TimeSpan Duration => TimeSpan.FromMinutes(Math.Clamp(DurationMinutes, 5, 24 * 60));

    /// <summary>Common appliances to start from, with typical run times and energy use.</summary>
    public static Appliance[] Suggestions() => new[]
    {
        new Appliance { Name = L.T("Dishwasher", "Opvaskemaskine"), DurationMinutes = 150, EnergyKwh = 1.0, FinishBy = 7 * 60 },
        new Appliance { Name = L.T("Washing machine", "Vaskemaskine"), DurationMinutes = 120, EnergyKwh = 0.9 },
        new Appliance { Name = L.T("Tumble dryer", "Tørretumbler"), DurationMinutes = 90, EnergyKwh = 2.5 },
        new Appliance { Name = L.T("Electric car", "Elbil"), DurationMinutes = 240, EnergyKwh = 30, FinishBy = 7 * 60 },
    };
}

/// <summary>A run the user asked LabWidge to start at the cheapest time. Kept until the run has finished.</summary>
internal sealed class PlannedRun
{
    public string ApplianceId { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    /// <summary>The latest end the run may be moved to when new prices arrive.</summary>
    public DateTime? Deadline { get; set; }
    /// <summary>The average price if the appliance had been started when the run was planned – for the savings.</summary>
    public double? PriceIfStartedNow { get; set; }
    public double Average { get; set; }
    public bool Started { get; set; }
    public bool Stopped { get; set; }
    /// <summary>The appliance as it was when the run started, so it is still switched off if the appliance is removed meanwhile.</summary>
    public Appliance? Snapshot { get; set; }
    /// <summary>The Home Assistant server the run was started on – it is only switched off there.</summary>
    public string? HomeAssistantUrl { get; set; }
}

/// <summary>The cheapest time to start an appliance, and what it costs compared with starting right away.</summary>
internal sealed record AppliancePlan(PriceWindow Best, double? NowAverage, DateTime? Deadline)
{
    /// <summary>The cost of a run in whole currency units, if the energy use is known.</summary>
    public double? Cost(Appliance a, PriceUnit unit) => a.EnergyKwh > 0 ? Best.Average * a.EnergyKwh / unit.PerMajor : null;

    /// <summary>How much less the cheapest time costs than starting now, in whole currency units.</summary>
    public double? Saving(Appliance a, PriceUnit unit) =>
        a.EnergyKwh > 0 && NowAverage is { } now ? (now - Best.Average) * a.EnergyKwh / unit.PerMajor : null;

    public bool StartsNow(DateTime now) => Best.Start <= now.AddMinutes(1);
}

internal static class AppliancePlanner
{
    /// <summary>When a run started now must be finished: the next time the clock reaches <see cref="Appliance.FinishBy"/>
    /// that leaves room for the whole run.</summary>
    public static DateTime? Deadline(Appliance a, DateTime now)
    {
        if (a.FinishBy is not int minutes) return null;
        var deadline = now.Date.AddMinutes(Math.Clamp(minutes, 0, 24 * 60 - 1));
        if (deadline < now + a.Duration) deadline = deadline.AddDays(1);
        return deadline;
    }

    public static AppliancePlan? Plan(Appliance a, IReadOnlyList<PricePeriod> periods, DateTime now, DateTime? deadline = null)
    {
        deadline ??= Deadline(a, now);
        var best = PriceWindows.Cheapest(periods, now, a.Duration, deadline, startNow: true);
        if (best == null) return null;
        return new AppliancePlan(best.Value, PriceWindows.Average(periods, now, now + a.Duration), deadline);
    }

    /// <summary>Plans a run of <paramref name="a"/> to start at the cheapest time. Null if the prices don't cover a whole run.</summary>
    public static PlannedRun? Arm(Appliance a, IReadOnlyList<PricePeriod> periods, DateTime now)
    {
        if (Plan(a, periods, now) is not { } plan) return null;
        return new PlannedRun
        {
            ApplianceId = a.Id, Start = plan.Best.Start, End = plan.Best.End, Deadline = plan.Deadline,
            PriceIfStartedNow = plan.NowAverage, Average = plan.Best.Average
        };
    }

    /// <summary>Raised when the user plans or cancels a run, so it can be started at once if the cheapest time is now.</summary>
    public static event Action? RunsChanged;

    /// <summary>Plans a run of <paramref name="a"/>, or cancels the one already planned. False if there were no prices to plan with.</summary>
    public static bool Toggle(AppSettings s, Appliance a, IReadOnlyList<PricePeriod> periods, DateTime now)
    {
        var existing = s.PlannedRuns.FirstOrDefault(r => r.ApplianceId == a.Id);
        if (existing != null) s.PlannedRuns.Remove(existing);
        else if (Arm(a, periods, now) is { } run) s.PlannedRuns.Add(run);
        else return false;
        RunsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Moves a run that has not started to a cheaper time if new prices (e.g. tomorrow's) make one possible before the deadline.
    /// True if the run was moved.
    /// </summary>
    public static bool Replan(PlannedRun run, Appliance a, IReadOnlyList<PricePeriod> periods, DateTime now)
    {
        if (run.Started || run.Start <= now) return false;
        var limit = run.Deadline ?? periods.LastOrDefault().End;
        if (PriceWindows.Cheapest(periods, now, a.Duration, limit, startNow: true) is not { } best) return false;
        if (best.Average >= run.Average - 0.01 || best.Start == run.Start) return false;
        run.Start = best.Start;
        run.End = best.End;
        run.Average = best.Average;
        return true;
    }

    /// <summary>"2 h 30 min".</summary>
    public static string FormatDuration(int minutes)
    {
        var h = minutes / 60;
        var m = minutes % 60;
        return h == 0 ? $"{m} min" : m == 0 ? L.T($"{h} h", $"{h} t") : L.T($"{h} h {m} min", $"{h} t {m} min");
    }
}

/// <summary>Amounts of money in the price country's currency.</summary>
internal static class Money
{
    public static string Format(double amount, string currency, IFormatProvider? culture = null)
    {
        culture ??= L.Culture;
        var value = amount.ToString(Math.Abs(amount) >= 100 ? "0" : "0.00", culture);
        return currency switch
        {
            "DKK" => $"{value} kr.",
            "SEK" or "NOK" => $"{value} kr",
            "EUR" => L.T($"€{value}", $"{value} €"),
            "PLN" => $"{value} zł",
            "CZK" => $"{value} Kč",
            "HUF" => $"{value} Ft",
            "RON" => $"{value} lei",
            "CHF" => $"CHF {value}",
            _ => $"{value} {currency}"
        };
    }

    public static string Format(double amount, PriceUnit unit) => Format(amount, unit.Currency);
}
