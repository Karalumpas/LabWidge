using System.Drawing;

// Plugin rendering uses the shared widget drawing context.
internal sealed partial class DashboardForm
{
    /// <summary>One line per Docker host with how many containers run, plus the containers that need attention.
    /// Click a host for the list of all containers.</summary>
    internal float DrawDocker(Graphics g, float x, float y, float w)
    {
        var s = _settings;
        var environments = _docker.Environments;
        var containers = environments.SelectMany(e => e.Containers).ToList();
        var running = containers.Count(c => c.IsRunning);
        var offline = environments.Where(e => !e.Up).ToList();
        var attention = containers.Count(c => c.NeedsAttention);

        string right;
        Color rightColor;
        if (_docker.LastError != null && environments.Count == 0) { right = "⚠ " + Shorten(_docker.LastError, 26); rightColor = _p.Amber; }
        else if (_docker.LastFetch == DateTime.MinValue) { right = L.T("fetching…", "henter…"); rightColor = _p.TextDim; }
        else if (offline.Count > 0) { right = $"⚠ {offline[0].Name} offline"; rightColor = _p.Red; }
        else if (attention > 0) { right = L.T($"⚠ {attention} need attention", $"⚠ {attention} kræver opmærksomhed"); rightColor = _p.Amber; }
        else { right = L.T($"{running}/{containers.Count} running", $"{running}/{containers.Count} kører"); rightColor = s.CollapsedDocker ? _p.TextSecondary : _p.TextDim; }

        y = Header(g, "", "DOCKER", right, rightColor, x, y, w,
                   s.CollapsedDocker, () => ToggleCollapsed(() => s.CollapsedDocker, v => s.CollapsedDocker = v));
        if (s.CollapsedDocker) return y - U(6);

        Action openPanel = () => SectionWindows.Toggle("docker", Bounds);
        if (environments.Count == 0)
        {
            var text = _docker.LastError ?? (_docker.LastFetch == DateTime.MinValue ? L.T("Fetching…", "Henter…") : L.T("Portainer has no Docker hosts", "Portainer har ingen Docker-værter"));
            DrawText(g, Fit(g, text, _f.Small, w), _f.Small, _docker.LastError != null ? _p.Amber : _p.TextDim, x, y);
            _hits.Add(new Hit(new RectangleF(x, y - U(2), w, U(20)), text + L.T("\nSee Settings → Docker", "\nSe Indstillinger → Docker"), null));
            return y + U(21) - U(2);
        }

        foreach (var env in environments)
        {
            var row = new RectangleF(x - U(6), y - U(3), w + U(12), U(22));
            var hovered = row.Contains(_mouse);
            if (hovered) FillRound(g, row, _p.HoverBg, U(5));
            var dot = !env.Up ? _p.Red : env.Attention > 0 ? _p.Amber : _p.Green;
            using (var brush = new SolidBrush(dot)) g.FillEllipse(brush, x + U(1), y + U(5), U(8), U(8));

            string value;
            if (!env.Up) value = "offline";
            else
            {
                value = L.T($"{env.Running} running", $"{env.Running} kører");
                if (env.Stopped > 0) value += L.T($" · {env.Stopped} stopped", $" · {env.Stopped} stoppet");
            }
            var vw = DrawText(g, value, _f.Small, !env.Up ? _p.Red : _p.TextSecondary, x + w, y + U(1), right: true);
            DrawText(g, Fit(g, env.Name, _f.Body, w - vw - U(26)), _f.Body, hovered ? _p.Blue : _p.TextPrimary, x + U(16), y);

            var tip = env.Name + "\n" + (!env.Up
                ? env.Error ?? L.T("Does not answer", "Svarer ikke")
                : string.Join("\n", env.Containers.Where(c => c.IsRunning).Take(8).Select(c => "• " + c.Name))
                  + (env.Running > 8 ? L.T($"\n… and {env.Running - 8} more", $"\n… og {env.Running - 8} mere") : ""))
                + L.T("\nClick for all containers", "\nKlik for alle containere");
            _hits.Add(new Hit(row, tip, openPanel));
            y += U(22);

            // The containers that need attention are named right under their host, at most two
            foreach (var problem in env.Containers.Where(c => c.NeedsAttention).Take(2))
            {
                var text = $"{problem.Name} · {DockerPanel.Describe(problem)}";
                DrawText(g, Fit(g, text, _f.Small, w - U(16)), _f.Small, _p.Amber, x + U(16), y);
                _hits.Add(new Hit(new RectangleF(x, y - U(2), w, U(18)), $"{problem.Name}\n{problem.Image}\n{problem.Status}", openPanel));
                y += U(19);
            }
        }
        return y - U(2);
    }
}
