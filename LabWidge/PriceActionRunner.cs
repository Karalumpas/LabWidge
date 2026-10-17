/// <summary>
/// Carries out what the electricity price decides: starts planned appliance runs (a reminder, or a Home Assistant entity)
/// and switches entities by the price rules. Runs on every price tick, i.e. every 30 seconds while LabWidge runs.
/// </summary>
internal sealed class PriceActionRunner
{
    /// <summary>A start more than this late (the PC was off or asleep) is skipped instead of carried out at a worse price.</summary>
    private static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(30);

    private readonly ElectricityPriceService _prices;
    private readonly HomeAssistantService _ha;
    private readonly Func<AppSettings> _settings;
    private readonly Action _save;
    private readonly Action<string, string, string, string> _toast;
    private readonly HashSet<string> _failedRules = new();
    private bool _busy;

    public PriceActionRunner(ElectricityPriceService prices, HomeAssistantService ha, Func<AppSettings> settings, Action save,
                             Action<string, string, string, string> toast)
    {
        _prices = prices;
        _ha = ha;
        _settings = settings;
        _save = save;
        _toast = toast;
    }

    public async Task TickAsync(CancellationToken cancel = default)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var s = _settings();
            if (!s.OnboardingCompleted || !s.PriceEnabled) return;
            var now = DateTime.Now;
            await RunPlannedAsync(s, now, cancel);
            await RunRulesAsync(s, now, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Error($"Price actions failed: {ex}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RunPlannedAsync(AppSettings s, DateTime now, CancellationToken cancel)
    {
        if (s.PlannedRuns.Count == 0) return;
        var periods = _prices.ConsumerPeriods(s);
        var unit = s.PriceUnit;
        var changed = false;
        foreach (var run in s.PlannedRuns.ToList())
        {
            var a = s.Appliances.FirstOrDefault(x => x.Id == run.ApplianceId);
            if (a == null)
            {
                s.PlannedRuns.Remove(run);
                changed = true;
                continue;
            }
            var entity = s.HasHomeAssistantConnection && !string.IsNullOrWhiteSpace(a.HomeAssistantEntity) ? a.HomeAssistantEntity : null;

            if (!run.Started)
            {
                if (now < run.Start)
                {
                    if (periods.Count > 0 && AppliancePlanner.Replan(run, a, periods, now))
                    {
                        changed = true;
                        Logger.Info($"Planned run of {a.Name} moved to {run.Start:g} – new prices made it cheaper.");
                    }
                    continue;
                }
                if (now - run.Start > MissedAfter)
                {
                    s.PlannedRuns.Remove(run);
                    changed = true;
                    _toast("appliance", L.T($"{a.Name} was not started", $"{a.Name} blev ikke startet"),
                        L.T($"The planned start at {run.Start:HH:mm} passed while LabWidge wasn't running.", $"Den planlagte start kl. {run.Start:HH:mm} gik, mens LabWidge ikke kørte."),
                        L.T("Plan it again in the widget.", "Planlæg den igen i widgetten."));
                    continue;
                }

                run.Started = true;
                changed = true;
                var cost = a.EnergyKwh > 0 ? L.T($" · about {Money.Format(run.Average * a.EnergyKwh / unit.PerMajor, unit)}", $" · ca. {Money.Format(run.Average * a.EnergyKwh / unit.PerMajor, unit)}") : "";
                var when = L.T($"Cheapest time: {run.Start:HH:mm}–{run.End:HH:mm}", $"Billigste tid: kl. {run.Start:HH:mm}–{run.End:HH:mm}") + cost;
                if (entity != null)
                {
                    try
                    {
                        await _ha.SetStateAsync(entity, true, s, cancel);
                        _toast("appliance", L.T($"{a.Name} started", $"{a.Name} er startet"), when,
                            L.T($"Home Assistant switched {entity} on", $"Home Assistant tændte {entity}"));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Logger.Error($"Home Assistant could not start {a.Name} ({entity}): {ex.Message}");
                        _toast("appliance", L.T($"Start {a.Name} now", $"Start {a.Name} nu"),
                            L.T("Home Assistant could not switch it on – start it yourself.", "Home Assistant kunne ikke tænde den – start den selv."), when);
                    }
                }
                else if (s.ApplianceReminders)
                {
                    _toast("appliance", L.T($"Time to start {a.Name}", $"Tid til at starte {a.Name}"), when,
                        L.T($"Finished around {run.End:HH:mm}", $"Færdig ca. kl. {run.End:HH:mm}"));
                }
                Logger.Info($"Planned run of {a.Name} started ({run.Start:g}–{run.End:t}).");
            }

            if (now >= run.End)
            {
                if (entity != null && a.TurnOffAfter && !run.Stopped)
                {
                    try { await _ha.SetStateAsync(entity, false, s, cancel); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Logger.Error($"Home Assistant could not switch {entity} off after {a.Name}: {ex.Message}");
                    }
                }
                run.Stopped = true;
                if (a.EnergyKwh > 0 && s.PriceHistoryEnabled)
                {
                    PriceHistory.AddSaving(new SavingRecord
                    {
                        Start = run.Start, Name = a.Name, EnergyKwh = a.EnergyKwh, Currency = unit.Currency,
                        Cost = run.Average * a.EnergyKwh / unit.PerMajor,
                        Saved = Math.Max(0, ((run.PriceIfStartedNow ?? run.Average) - run.Average) * a.EnergyKwh / unit.PerMajor)
                    });
                }
                s.PlannedRuns.Remove(run);
                changed = true;
            }
        }
        if (changed) _save();
    }

    private async Task RunRulesAsync(AppSettings s, DateTime now, CancellationToken cancel)
    {
        if (!s.HasHomeAssistantConnection || s.PriceAutomations.Count == 0)
        {
            if (s.AutomationStates.Count > 0 && s.PriceAutomations.Count == 0)
            {
                s.AutomationStates.Clear();
                _save();
            }
            return;
        }
        var periods = _prices.ConsumerPeriods(s);
        var hourValues = _prices.HourlyConsumer(s).Select(h => h.Value).ToList();
        var changed = false;
        var current = new HashSet<string>();
        foreach (var rule in s.PriceAutomations.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.EntityId)))
        {
            var key = rule.Signature;
            current.Add(key);
            if (AutomationRules.Applies(rule, periods, hourValues, now) is not bool applies) continue;
            bool? previous = s.AutomationStates.TryGetValue(key, out var p) ? p : null;
            if (AutomationRules.Action(rule, applies, previous) is bool on)
            {
                try
                {
                    await _ha.SetStateAsync(rule.EntityId, on, s, cancel);
                    _failedRules.Remove(rule.Id);
                    Logger.Info($"Price rule \"{rule.Name}\": {rule.EntityId} {(on ? "on" : "off")}.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not remembered, so it is tried again at the next check
                    Logger.Error($"Price rule \"{rule.Name}\" could not switch {rule.EntityId}: {ex.Message}");
                    if (_failedRules.Add(rule.Id))
                        _toast("automation", L.T("A price rule could not run", "En prisregel kunne ikke udføres"),
                            L.T($"\"{rule.Name}\" could not switch {rule.EntityId} {(on ? "on" : "off")}.", $"\"{rule.Name}\" kunne ikke {(on ? "tænde" : "slukke")} {rule.EntityId}."),
                            HomeAssistantService.Describe(ex));
                    continue;
                }
            }
            if (previous != applies)
            {
                s.AutomationStates[key] = applies;
                changed = true;
            }
        }
        foreach (var stale in s.AutomationStates.Keys.Where(k => !current.Contains(k)).ToList())
        {
            s.AutomationStates.Remove(stale);
            changed = true;
        }
        if (changed) _save();
    }
}
