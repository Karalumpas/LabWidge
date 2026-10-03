using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

/// <summary>
/// The panel behind the Proxmox section: all VMs and containers with status and usage. Clicking a machine gives
/// a menu with start, shut down, reboot and "open in Proxmox".
/// </summary>
internal sealed class ProxmoxPanel : PopupPanel
{
    private static CultureInfo Fmt => L.Culture;

    private static ProxmoxPanel? _open;
    private static DateTime _closedAt = DateTime.MinValue;

    private readonly ProxmoxService _pve;
    private readonly AppSettings _settings;
    private readonly ContextMenuStrip _menu = new();

    public static bool IsOpen => _open is { IsDisposed: false, Visible: true };

    public static void Toggle(ProxmoxService pve, AppSettings settings, Rectangle near)
    {
        if (IsOpen)
        {
            _open!.Close();
            return;
        }
        if ((DateTime.Now - _closedAt).TotalMilliseconds < 250) return;

        var panel = new ProxmoxPanel(pve, settings);
        _open = panel;
        panel.ShowNear(near);
        _ = pve.RefreshAsync(settings);
    }

    public static void CloseIfOpen()
    {
        if (IsOpen) _open!.Close();
    }

    private ProxmoxPanel(ProxmoxService pve, AppSettings settings) : base("Proxmox", settings, 380)
    {
        _pve = pve;
        _settings = settings;
        _pve.Updated += OnDataUpdated;
        _menu.Closed += (_, _) => KeepOpen = false;
    }

    private void OnDataUpdated() => RequestRedraw();

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closedAt = DateTime.Now;
        if (ReferenceEquals(_open, this)) _open = null;
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pve.Updated -= OnDataUpdated;
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- Actions ----------

    private void ShowActions(PveGuest guest)
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem($"{guest.Kind} {guest.VmId}  ·  {guest.Name}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        if (guest.IsRunning)
        {
            _menu.Items.Add(L.T("Shut down", "Luk ned"), null, (_, _) => Confirm(guest, "shutdown",
                L.T($"Shut down {guest.Name} ({guest.Kind} {guest.VmId})?", $"Vil du lukke {guest.Name} ({guest.Kind} {guest.VmId}) ned?")));
            _menu.Items.Add(L.T("Reboot", "Genstart"), null, (_, _) => Confirm(guest, "reboot",
                L.T($"Reboot {guest.Name} ({guest.Kind} {guest.VmId})?", $"Vil du genstarte {guest.Name} ({guest.Kind} {guest.VmId})?")));
        }
        else
        {
            _menu.Items.Add(L.T("Start", "Start"), null, async (_, _) => await RunAsync(guest, "start"));
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(L.T("Open in Proxmox", "Åbn i Proxmox"), null, (_, _) => Open(_pve.WebUrl(guest)));

        KeepOpen = true;
        _menu.Show(this, PointToClient(Cursor.Position));
    }

    private async void Confirm(PveGuest guest, string action, string question)
    {
        KeepOpen = true;
        var answer = MessageBox.Show(this, question,
            "Proxmox", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        KeepOpen = false;
        if (answer == DialogResult.Yes) await RunAsync(guest, action);
        if (!IsDisposed) Activate();
    }

    private async Task RunAsync(PveGuest guest, string action)
    {
        var error = await _pve.PowerAsync(guest, action);
        if (error != null && !IsDisposed)
        {
            KeepOpen = true;
            MessageBox.Show(this, error, "Proxmox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            KeepOpen = false;
            return;
        }

        // The status is followed closely until the action has taken effect
        foreach (var delay in new[] { 3, 8, 15, 30 })
        {
            await Task.Delay(TimeSpan.FromSeconds(delay));
            await _pve.RefreshAsync(_settings);
            if (_pve.PendingAction(guest.VmId) == null) break;
        }
    }

    // ---------- Drawing ----------

    protected override float RenderContent(Graphics g, float x, float y, float w)
    {
        var updated = _pve.LastFetch == DateTime.MinValue ? L.T("fetching…", "henter…") : L.T("updated ", "opdateret ") + _pve.LastFetch.ToString("HH:mm:ss");
        y = Header(g, "", "PROXMOX", updated, x, y, w);

        if (_pve.LastError != null)
        {
            y = Wrapped(g, "⚠ " + _pve.LastError, F.Small, P.Amber, x, y, w);
        }

        foreach (var node in _pve.Nodes)
        {
            Dot(g, node.Online ? P.Green : P.Red, x + U(1), y + U(5), 9);
            DrawText(g, node.Name, F.BodyBold, P.TextPrimary, x + U(18), y);
            DrawText(g, node.Online ? L.T("up ", "oppe ") + Uptime(node.Uptime) : "offline", F.Small, node.Online ? P.TextSecondary : P.Red, x + w, y + U(1), right: true);
            y += U(20);
            if (node.Online)
            {
                var line = $"CPU {node.Cpu * 100:0} % " + L.T("of", "af") + $" {node.MaxCpu}  ·  RAM {Gb(node.Mem)}/{Gb(node.MaxMem)} GB  ·  disk {Gb(node.Disk)}/{Gb(node.MaxDisk)} GB";
                DrawText(g, Fit(g, line, F.Small, w - U(18)), F.Small, P.TextDim, x + U(18), y);
                AddHit(new RectangleF(x, y - U(2), w, U(18)), line);
                y += U(21);
            }
        }

        var groups = new[] { (L.T("RUNNING", "KØRER"), _pve.Guests.Where(gu => gu.IsRunning).ToList()), (L.T("STOPPED", "STOPPET"), _pve.Guests.Where(gu => !gu.IsRunning).ToList()) };
        foreach (var (title, guests) in groups)
        {
            if (guests.Count == 0) continue;
            y += U(4);
            y = Divider(g, x, y, w);
            y = Subheading(g, $"{title}  ·  {guests.Count}", x, y);
            foreach (var guest in guests) y = DrawGuest(g, guest, x, y, w);
        }

        if (_pve.Storages.Count > 0)
        {
            y += U(4);
            y = Divider(g, x, y, w);
            y = Subheading(g, L.T("STORAGE", "LAGER"), x, y);
            foreach (var s in _pve.Storages)
            {
                var frac = s.Total == 0 ? 0 : (double)s.Used / s.Total;
                var color = frac > 0.9 ? P.Red : frac > 0.8 ? P.Amber : P.Blue;
                var value = $"{Gb(s.Used)} / {Gb(s.Total)} GB";
                var vw = DrawText(g, value, F.Small, P.TextSecondary, x + w, y + U(1), right: true);
                DrawText(g, s.Name, F.Body, P.TextPrimary, x, y);
                var bar = new RectangleF(x + U(110), y + U(7), w - U(110) - vw - U(10), U(5));
                FillRound(g, bar, P.Track, bar.Height / 2);
                FillRound(g, new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * (float)Math.Clamp(frac, 0, 1)), bar.Height), color, bar.Height / 2);
                AddHit(new RectangleF(x, y - U(2), w, U(20)), s.Name + (s.Shared ? L.T(" (shared)", " (delt)") : L.T(" on ", " på ") + s.Node)
                                                            + L.T($": {frac * 100:0} % used", $": {frac * 100:0} % brugt"));
                y += U(22);
            }
        }

        y += U(4);
        y = Divider(g, x, y, w);
        return LinkRow(g, L.T("Open Proxmox", "Åbn Proxmox"), _pve.WebUrl(), x, y, w);
    }

    private float DrawGuest(Graphics g, PveGuest guest, float x, float y, float w)
    {
        var row = new RectangleF(x - U(6), y - U(3), w + U(12), U(22));
        var hovered = row.Contains(Mouse);
        if (hovered) FillRound(g, row, P.HoverBg, U(5));

        var pending = _pve.PendingAction(guest.VmId);
        var color = pending != null ? P.Amber : guest.IsRunning ? P.Green : guest.Status == "paused" ? P.Amber : P.Track;
        Dot(g, color, x + U(1), y + U(6), 7);

        string right;
        if (pending != null) right = pending switch { "start" => L.T("starting…", "starter…"), "shutdown" => L.T("shutting down…", "lukker ned…"), "reboot" => L.T("rebooting…", "genstarter…"), _ => "…" };
        else if (guest.IsRunning) right = $"{guest.Cpu * 100:0} %  ·  {Gb(guest.Mem)}/{Gb(guest.MaxMem)} GB";
        else right = guest.Status == "stopped" ? "stoppet" : guest.Status;
        var rw = DrawText(g, right, F.Small, pending != null ? P.Amber : P.TextSecondary, x + w, y + U(1), right: true);

        var idW = DrawText(g, guest.VmId.ToString(), F.Small, P.TextDim, x + U(16), y + U(1));
        var kind = guest.Kind;
        var kx = x + U(16) + Math.Max(idW, U(26)) + U(6);
        var kw = DrawText(g, kind, F.Tiny, P.TextDim, kx, y + U(2));
        var nx = kx + kw + U(6);
        DrawText(g, Fit(g, guest.Name, F.Body, x + w - rw - U(10) - nx), F.Body, hovered ? P.Blue : guest.IsRunning ? P.TextPrimary : P.TextSecondary, nx, y);

        var tip = $"{guest.Name} ({(guest.Type == "lxc" ? "container" : L.T("virtual machine", "virtuel maskine"))} {guest.VmId}" + L.T(" on ", " på ") + guest.Node + ")\n"
                  + (guest.IsRunning ? L.T($"Up {Uptime(guest.Uptime)} · {guest.MaxCpu} cores\n", $"Oppe {Uptime(guest.Uptime)} · {guest.MaxCpu} kerner\n") : "")
                  + L.T("Click to start, shut down or reboot", "Klik for start, luk ned eller genstart");
        AddHit(row, tip, pending == null ? () => ShowActions(guest) : null);
        return y + U(22);
    }

    private static string Gb(long bytes)
    {
        var gb = bytes / 1073741824.0;
        return gb >= 100 ? gb.ToString("#,0", Fmt) : gb.ToString("0.0", Fmt);
    }

    internal static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} " + L.T("h", "t")
        : t.TotalHours >= 1 ? $"{t.Hours} " + L.T("h", "t") + $" {t.Minutes} m"
        : $"{Math.Max(0, t.Minutes)} m";
}
