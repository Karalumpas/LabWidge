using System.Drawing;

// Plugin rendering uses the shared widget drawing context.
internal sealed partial class DashboardForm
{
    internal float DrawProxmox(Graphics g, float x, float y, float w)
    {
        var s = _settings;
        var guests = _pve.Guests;
        var running = guests.Count(gu => gu.IsRunning);
        var offline = _pve.Nodes.Where(n => !n.Online).ToList();

        string right;
        Color rightColor;
        if (_pve.LastError != null && _pve.Nodes.Count == 0) { right = "⚠ " + Shorten(_pve.LastError, 26); rightColor = _p.Amber; }
        else if (_pve.LastFetch == DateTime.MinValue) { right = L.T("fetching…", "henter…"); rightColor = _p.TextDim; }
        else if (offline.Count > 0) { right = $"⚠ {offline[0].Name} offline"; rightColor = _p.Red; }
        else { right = L.T($"{running}/{guests.Count} running", $"{running}/{guests.Count} kører"); rightColor = s.CollapsedProxmox ? _p.TextSecondary : _p.TextDim; }

        y = Header(g, "", "PROXMOX", right, rightColor, x, y, w,
                   s.CollapsedProxmox, () => ToggleCollapsed(() => s.CollapsedProxmox, v => s.CollapsedProxmox = v));
        if (s.CollapsedProxmox) return y - U(6);

        Action openPanel = () => SectionWindows.Toggle("proxmox", Bounds);
        var labelW = U(46);
        var valueW = U(112);

        if (_pve.Nodes.Count == 0)
        {
            var text = _pve.LastError ?? L.T("Fetching…", "Henter…");
            DrawText(g, Fit(g, text, _f.Small, w), _f.Small, _pve.LastError != null ? _p.Amber : _p.TextDim, x, y);
            _hits.Add(new Hit(new RectangleF(x, y - U(2), w, U(20)), text + L.T("\nSee Settings → Proxmox", "\nSe Indstillinger → Proxmox"), null));
            y += U(21);
        }

        foreach (var node in _pve.Nodes)
        {
            if (_pve.Nodes.Count > 1 || !node.Online)
            {
                DrawText(g, node.Name, _f.SmallBold, node.Online ? _p.TextSecondary : _p.Red, x, y);
                DrawText(g, node.Online ? L.T("up ", "oppe ") + ProxmoxPanel.Uptime(node.Uptime) : "offline", _f.Small, _p.TextDim, x + w, y, right: true);
                y += U(19);
            }
            if (!node.Online) continue;

            var nodeTip = $"{node.Name} · " + L.T("up ", "oppe ") + ProxmoxPanel.Uptime(node.Uptime);
            y = BarRow(g, "CPU", node.Cpu, L.T($"{node.Cpu * 100:0} % of {node.MaxCpu}", $"{node.Cpu * 100:0} % af {node.MaxCpu}"), x, y, w, labelW, valueW, nodeTip, openPanel);
            var mem = node.MaxMem == 0 ? 0 : (double)node.Mem / node.MaxMem;
            y = BarRow(g, "RAM", mem, $"{Gb(node.Mem)} / {Gb(node.MaxMem)} GB", x, y, w, labelW, valueW, nodeTip, openPanel);
        }

        // The largest storage – the others are in the panel
        if (_pve.Storages.FirstOrDefault() is { } storage)
        {
            var frac = storage.Total == 0 ? 0 : (double)storage.Used / storage.Total;
            y = BarRow(g, L.T("Storage", "Lager"), frac, $"{Gb(storage.Used)} / {Gb(storage.Total)} GB", x, y, w, labelW, valueW,
                       $"{storage.Name}: {frac * 100:0} % brugt" + (_pve.Storages.Count > 1 ? $"\n{_pve.Storages.Count - 1} lagre mere i panelet" : ""), openPanel);
        }

        // Machines: running and stopped, click for the list
        if (guests.Count > 0)
        {
            var link = new RectangleF(x - U(6), y - U(1), w + U(12), U(22));
            var hovered = link.Contains(_mouse);
            if (hovered) FillRound(g, link, _p.HoverBg, U(5));
            var stopped = guests.Count - running;
            DrawText(g, "VM/CT", _f.Body, hovered ? _p.Blue : _p.TextSecondary, x, y + U(1));
            var tx = x + labelW;
            tx += DrawText(g, L.T($"{running} running", $"{running} kører"), _f.Body, _p.TextPrimary, tx, y + U(1));
            if (stopped > 0) DrawText(g, $"  ·  {stopped} stoppet", _f.Body, _p.TextDim, tx, y + U(1));
            DrawText(g, "", _f.IconSmall, _p.TextDim, x + w - U(10), y + U(5));

            var busiest = guests.Where(gu => gu.IsRunning).OrderByDescending(gu => gu.Cpu).Take(3)
                .Select(gu => $"{gu.Name}: CPU {gu.Cpu * 100:0} %, RAM {Gb(gu.Mem)} GB");
            _hits.Add(new Hit(link, L.T("Busiest right now:\n", "Travleste lige nu:\n") + string.Join("\n", busiest)
                + L.T("\nClick for all machines – start, shut down and reboot", "\nKlik for alle maskiner – start, luk ned og genstart"), openPanel));
            y += U(22);
        }
        return y - U(2);
    }
}
