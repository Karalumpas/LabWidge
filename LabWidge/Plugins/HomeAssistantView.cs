using System.Drawing;

// Plugin rendering uses the shared widget drawing context.
internal sealed partial class DashboardForm
{
    internal float DrawHomeAssistant(Graphics g, float x, float y, float w)
    {
        var entities = _ha.Entities;
        var lit = entities.Count(e => e.CanToggle && e.IsOn);

        string right;
        Color rightColor;
        if (_ha.LastError != null)
        {
            right = "⚠ " + Shorten(_ha.LastError, 20);
            rightColor = _p.Amber;
        }
        else if (entities.Count == 0)
        {
            right = L.T("fetching…", "henter…");
            rightColor = _p.TextDim;
        }
        else
        {
            right = lit == 0 ? L.T("all off", "alt slukket") : L.T($"{lit} on", $"{lit} tændt");
            rightColor = lit > 0 ? _p.Amber : _p.TextSecondary;
        }

        y = Header(g, "", "HOME ASSISTANT", right, rightColor, x, y, w, _settings.CollapsedHomeAssistant,
            () => ToggleCollapsed(() => _settings.CollapsedHomeAssistant, value => _settings.CollapsedHomeAssistant = value));
        if (_settings.CollapsedHomeAssistant) return y - U(6);

        foreach (var entity in entities.Take(6))
        {
            var pending = _ha.IsPending(entity.EntityId);
            var clickable = entity.CanToggle && !entity.IsUnavailable && !pending;
            var rect = new RectangleF(x - U(4), y - U(2), w + U(8), U(24));
            if (clickable && rect.Contains(_mouse)) FillRound(g, rect, _p.HoverBg, U(4));
            var value = pending ? L.T("sending…", "sender…") : entity.Display();
            var valueWidth = Measure(g, value, _f.SmallBold).Width;
            DrawText(g, Fit(g, entity.Name, _f.Body, Math.Max(U(30), w - valueWidth - U(12))), _f.Body,
                entity.IsUnavailable ? _p.TextDim : _p.TextSecondary, x, y);
            DrawText(g, value, _f.SmallBold, entity.IsUnavailable ? _p.Amber : entity.IsOn ? _p.Green : _p.TextDim, x + w, y, right: true);
            _hits.Add(new Hit(rect, entity.EntityId, clickable ? () => _ = _ha.ToggleAsync(entity.EntityId, _settings) : null));
            y += U(24);
        }
        var text = _settings.HomeAssistantDashboardPath is { Length: > 0 }
            ? L.T("Open dashboard", "Åbn dashboard") : L.T("Open all entities", "Åbn alle entiteter");
        var link = new RectangleF(x, y, w, U(22));
        DrawText(g, text, _f.Small, _p.Blue, x, y);
        _hits.Add(new Hit(link, text, () => SectionWindows.Toggle("ha", Bounds)));
        return y + U(22);
    }
}
