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
            if (!s.OnboardingCompleted) return;
            var now = DateTime.Now;
            // With the price switched off nothing new starts, but a run already started is still switched off when it ends
            await RunPlannedAsync(s, now, cancel, startNew: s.PriceEnabled);
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

    private async Task RunPlannedAsync(AppSettings s, DateTime now, CancellationToken cancel, bool startNew)
    {
        if (s.PlannedRuns.Count == 0) return;
        var periods = _prices.ConsumerPeriods(s);
        var unit = s.PriceUnit;
        var changed = false;
        foreach (var run in s.PlannedRuns.ToList())
        {
            // Once started, the run follows the appliance as it was then: an edit or a removal must not change what is switched off
            var a = run.Started && run.Snapshot != null ? run.Snapshot : s.Appliances.FirstOrDefault(x => x.Id == run.ApplianceId);
            if (a == null)
            {
                s.PlannedRuns.Remove(run);
                changed = true;
                continue;
            }
            // A run already started is switched off through Home Assistant even if its plugin was switched off since
            var reachable = s.HasHomeAssistantConnection || (run.Started && !string.IsNullOrWhiteSpace(s.HomeAssistantUrl));
            var entity = reachable && !string.IsNullOrWhiteSpace(a.HomeAssistantEntity) ? a.HomeAssistantEntity : null;

            if (!run.Started)
            {
                if (!startNew) continue;
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

                // A late start (the PC was asleep) still gets the whole run time
                if (now > run.Start)
                {
                    run.End = now + a.Duration;
                    run.Start = now;
                    if (PriceWindows.Average(periods, run.Start, run.End) is { } average) run.Average = average;
                }
                run.Started = true;
                run.HomeAssistantUrl = entity != null ? s.HomeAssistantUrl : null;
                run.Snapshot = new Appliance
                {
                    Id = a.Id, Name = a.Name, DurationMinutes = a.DurationMinutes, EnergyKwh = a.EnergyKwh,
                    HomeAssistantEntity = a.HomeAssistantEntity, TurnOffAfter = a.TurnOffAfter
                };
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
                if (entity != null && a.TurnOffAfter && !run.Stopped && run.HomeAssistantUrl != null
                    && !string.Equals(HomeAssistantService.Normalize(run.HomeAssistantUrl), HomeAssistantService.Normalize(s.HomeAssistantUrl ?? ""), StringComparison.OrdinalIgnoreCase))
                {
                    // The Home Assistant address was changed during the run: never switch off something on another server
                    _toast("appliance", L.T($"Switch {a.Name} off", $"Sluk {a.Name}"),
                        L.T($"The Home Assistant address changed while {entity} was on, so LabWidge didn't switch it off.",
                            $"Home Assistant-adressen blev ændret, mens {entity} var tændt, så LabWidge slukkede den ikke."),
                        run.HomeAssistantUrl);
                    run.Stopped = true;
                }
                if (entity != null && a.TurnOffAfter && !run.Stopped)
                {
                    try { await _ha.SetStateAsync(entity, false, s, cancel); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Logger.Error($"Home Assistant could not switch {entity} off after {a.Name}: {ex.Message}");
                        // Kept, so the next check tries again – until it has failed for a while, then the user is told
                        if (now - run.End < MissedAfter) continue;
                        _toast("appliance", L.T($"Switch {a.Name} off", $"Sluk {a.Name}"),
                            L.T($"Home Assistant could not switch {entity} off after the run – it may still be on.",
                                $"Home Assistant kunne ikke slukke {entity} efter kørslen – den er måske stadig tændt."),
                            HomeAssistantService.Describe(ex));
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
        var url = string.IsNullOrWhiteSpace(s.HomeAssistantUrl) ? null : HomeAssistantService.Normalize(s.HomeAssistantUrl);
        var running = s.PriceEnabled && s.HasHomeAssistantConnection;
        var active = running
            ? s.PriceAutomations.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.EntityId)).ToList()
            : new List<PriceAutomation>();
        var changed = false;
        var waiting = new HashSet<string>();

        // A rule that was removed, switched off, edited or moved to another server – or that can't run any more – first puts
        // back what it switched, on the server it switched it on
        foreach (var (id, state) in s.RuleStates.ToList())
        {
            var rule = active.FirstOrDefault(r => r.Id == id);
            if (rule != null && state.Signature == rule.Signature && state.Url == url) continue;
            if (state.Applies && state.Revert)
            {
                if (state.Url != null && state.Url == url)
                {
                    if (!await SwitchAsync(s, id, state.Name, state.EntityId, !state.TurnOn, cancel))
                    {
                        waiting.Add(id); // kept and tried again at the next check; the rule waits until its old entity is back
                        continue;
                    }
                }
                else
                {
                    _toast("automation", L.T($"Switch {state.EntityId} back", $"Sæt {state.EntityId} tilbage"),
                        L.T($"The Home Assistant address changed while \"{state.Name}\" applied, so LabWidge didn't switch it back.",
                            $"Home Assistant-adressen blev ændret, mens \"{state.Name}\" gjaldt, så LabWidge satte den ikke tilbage."),
                        state.Url ?? "");
                }
            }
            s.RuleStates.Remove(id);
            changed = true;
        }

        if (active.Count > 0)
        {
            var periods = _prices.ConsumerPeriods(s);
            var hourValues = _prices.HourlyConsumer(s).Select(h => h.Value).ToList();
            foreach (var rule in active.Where(r => !waiting.Contains(r.Id)))
            {
                bool? previous = s.RuleStates.TryGetValue(rule.Id, out var state) ? state.Applies : null;
                // Without a price for now (the prices ran out), a rule that applied stops applying, so the entity is switched back
                if ((AutomationRules.Applies(rule, periods, hourValues, now) ?? (previous == true ? false : null)) is not bool applies) continue;
                if (AutomationRules.Action(rule, applies, previous) is bool on
                    && !await SwitchAsync(s, rule.Id, rule.Name, rule.EntityId, on, cancel)) continue; // not remembered, so tried again
                if (previous != applies)
                {
                    s.RuleStates[rule.Id] = new RuleState
                    {
                        Signature = rule.Signature, Applies = applies, Name = rule.Name, EntityId = rule.EntityId,
                        TurnOn = rule.TurnOn, Revert = rule.Revert, Url = url
                    };
                    changed = true;
                }
            }
        }
        if (changed) _save();
    }

    /// <summary>Switches an entity for a price rule. False (and a notification the first time) if Home Assistant refused.</summary>
    private async Task<bool> SwitchAsync(AppSettings s, string ruleId, string name, string entityId, bool on, CancellationToken cancel)
    {
        try
        {
            await _ha.SetStateAsync(entityId, on, s, cancel);
            _failedRules.Remove(ruleId);
            Logger.Info($"Price rule \"{name}\": {entityId} {(on ? "on" : "off")}.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error($"Price rule \"{name}\" could not switch {entityId}: {ex.Message}");
            if (_failedRules.Add(ruleId))
                _toast("automation", L.T("A price rule could not run", "En prisregel kunne ikke udføres"),
                    L.T($"\"{name}\" could not switch {entityId} {(on ? "on" : "off")}.", $"\"{name}\" kunne ikke {(on ? "tænde" : "slukke")} {entityId}."),
                    HomeAssistantService.Describe(ex));
            return false;
        }
    }
}
