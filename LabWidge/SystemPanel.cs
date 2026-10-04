using System.Diagnostics;
using System.Drawing;
using System.Globalization;

/// <summary>
/// The system window: CPU and RAM with five minutes of history, the graphics card in detail, every fixed disk
/// (click to open it) and the processes using the most memory.
/// </summary>
internal sealed class SystemPanel : PopupPanel
{
    private static CultureInfo Fmt => L.Culture;

    private readonly SystemMonitor _sys;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private IReadOnlyList<(string Name, long Bytes, int Count)> _processes = Array.Empty<(string, long, int)>();
    private int _ticks;

    public SystemPanel(SystemMonitor sys, AppSettings settings, Action saveSettings)
        : base("system", "", "SYSTEM", settings, saveSettings, 460)
    {
        _sys = sys;
        _tick.Tick += (_, _) =>
        {
            // The widget samples every second; the process list is heavier and is read every 5 seconds
            if (_ticks++ % 5 == 0) _ = ReadProcessesAsync();
            Invalidate();
        };
        _tick.Start();
        _ = ReadProcessesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }

    private async Task ReadProcessesAsync()
    {
        var list = await Task.Run(() =>
        {
            var result = new Dictionary<string, (long Bytes, int Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var (bytes, count) = result.GetValueOrDefault(p.ProcessName);
                    result[p.ProcessName] = (bytes + p.WorkingSet64, count + 1);
                }
                catch
                {
                    // The process exited or is protected
                }
                finally
                {
                    p.Dispose();
                }
            }
            return result.OrderByDescending(r => r.Value.Bytes).Take(6).Select(r => (r.Key, r.Value.Bytes, r.Value.Count)).ToList();
        });
        _processes = list;
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Invalidate));
    }

    protected override string HeaderStatus => L.T("up ", "tændt i ") + Uptime(_sys.Uptime);

    private static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? L.T($"{(int)t.TotalDays} d {t.Hours} h", $"{(int)t.TotalDays} d {t.Hours} t")
        : t.TotalHours >= 1 ? L.T($"{(int)t.TotalHours} h {t.Minutes} m", $"{(int)t.TotalHours} t {t.Minutes} m")
        : $"{t.Minutes} m";

    private static string Gb(double bytes) => (bytes / (1L << 30)).ToString(bytes >= 100L << 30 ? "0" : "0.0", Fmt);

    protected override float RenderContent(Graphics g, float x, float y, float w)
    {
        // ---- CPU ----
        y = Subheading(g, "CPU  ·  " + _sys.CpuName + L.T($"  ·  {Environment.ProcessorCount} threads", $"  ·  {Environment.ProcessorCount} tråde"), x, y);
        DrawText(g, $"{_sys.CpuPercent:0} %", F.BodyBold, P.TextPrimary, x, y);
        var cpu = _sys.CpuHistory.Tail(History.FiveMinutes);
        DrawText(g, L.T($"avg {cpu.DefaultIfEmpty().Average():0} %  ·  max {cpu.DefaultIfEmpty().Max():0} %  ·  last 5 min",
                        $"gns. {cpu.DefaultIfEmpty().Average():0} %  ·  maks {cpu.DefaultIfEmpty().Max():0} %  ·  sidste 5 min"), F.Small, P.TextDim, x + w, y + U(1), right: true);
        y += U(22);
        LineChart(g, new RectangleF(x, y, w, U(70)), cpu, 100, P.Blue);
        y += U(82);

        // ---- RAM ----
        var ramFrac = _sys.RamTotal == 0 ? 0 : _sys.RamUsed / (double)_sys.RamTotal;
        y = Subheading(g, "RAM", x, y);
        DrawText(g, $"{Gb(_sys.RamUsed)} / {Gb(_sys.RamTotal)} GB", F.BodyBold, P.TextPrimary, x, y);
        DrawText(g, L.T($"{ramFrac * 100:0} % in use  ·  {Gb(_sys.RamTotal - _sys.RamUsed)} GB free", $"{ramFrac * 100:0} % i brug  ·  {Gb(_sys.RamTotal - _sys.RamUsed)} GB fri"),
                 F.Small, P.TextDim, x + w, y + U(1), right: true);
        y += U(22);
        LineChart(g, new RectangleF(x, y, w, U(44)), _sys.RamHistory.Tail(History.FiveMinutes), 100, ramFrac > 0.9 ? P.Red : ramFrac > 0.8 ? P.Amber : P.Blue);
        y += U(56);

        // ---- GPU ----
        var gpu = _sys.Gpu;
        if (gpu.Available)
        {
            y = Subheading(g, "GPU  ·  " + gpu.Name, x, y);
            DrawText(g, $"{gpu.Percent:0} %", F.BodyBold, P.TextPrimary, x, y);
            var details = new List<string>();
            if (gpu.TempC is int t) details.Add($"{t} °C");
            if (gpu.PowerW is double pw) details.Add(gpu.PowerLimitW is double lim ? $"{pw:0} / {lim:0} W" : $"{pw:0} W");
            if (gpu.FanPercent is int fan) details.Add(L.T($"fan {fan} %", $"blæser {fan} %"));
            if (gpu.ClockMhz is int mhz) details.Add($"{mhz} MHz");
            DrawText(g, string.Join("  ·  ", details), F.Small, gpu.TempC is >= 80 ? P.Red : gpu.TempC is >= 70 ? P.Amber : P.TextDim, x + w, y + U(1), right: true);
            y += U(22);
            LineChart(g, new RectangleF(x, y, w, U(44)), gpu.History.Tail(History.FiveMinutes), 100, P.Green);
            y += U(54);
            if (gpu.VramTotal > 0)
            {
                DrawText(g, "VRAM", F.Body, P.TextSecondary, x, y);
                Bar(g, new RectangleF(x + U(56), y + U(6), w - U(56) - U(118), U(6)), gpu.VramUsed / (double)gpu.VramTotal);
                DrawText(g, $"{Gb(gpu.VramUsed)} / {Gb(gpu.VramTotal)} GB", F.BodyBold, P.TextPrimary, x + w, y, right: true);
                y += U(24);
            }
            if (!string.IsNullOrEmpty(gpu.Source))
            {
                DrawText(g, L.T("Read via ", "Læst via ") + gpu.Source, F.Tiny, P.TextDim, x, y);
                y += U(18);
            }
            y += U(6);
        }

        // ---- Disks ----
        y = Subheading(g, L.T("DISKS  ·  click to open", "DISKE  ·  klik for at åbne"), x, y);
        foreach (var d in _sys.Disks)
        {
            var frac = d.Total == 0 ? 0 : d.Used / (double)d.Total;
            var row = new RectangleF(x - U(6), y - U(3), w + U(12), U(24));
            var hovered = row.Contains(Mouse);
            if (hovered) FillRound(g, row, P.HoverBg, U(5));
            var name = string.IsNullOrWhiteSpace(d.Label) ? d.Name : $"{d.Name} {d.Label}";
            DrawText(g, Fit(g, name, F.Body, U(110)), F.Body, hovered ? P.Blue : P.TextSecondary, x, y);
            Bar(g, new RectangleF(x + U(118), y + U(6), w - U(118) - U(150), U(6)), frac);
            DrawText(g, $"{Gb(d.Used)} / {Gb(d.Total)} GB", F.BodyBold, P.TextPrimary, x + w, y, right: true);
            var drive = d.Name;
            AddHit(row, L.T($"{Gb(d.Total - d.Used)} GB free ({(1 - frac) * 100:0} %) – click to open {drive}",
                            $"{Gb(d.Total - d.Used)} GB fri ({(1 - frac) * 100:0} %) – klik for at åbne {drive}"),
                () => Open(drive.EndsWith('\\') ? drive : drive + "\\"));
            y += U(24);
        }

        // ---- Processes ----
        if (_processes.Count > 0)
        {
            y += U(6);
            y = Subheading(g, L.T("USING THE MOST MEMORY", "BRUGER MEST HUKOMMELSE"), x, y);
            var top = Math.Max(1, _processes[0].Bytes);
            foreach (var (name, bytes, count) in _processes)
            {
                var label = count > 1 ? $"{name} ({count})" : name;
                DrawText(g, Fit(g, label, F.Body, U(170)), F.Body, P.TextSecondary, x, y);
                Bar(g, new RectangleF(x + U(178), y + U(6), w - U(178) - U(80), U(5)), bytes / (double)top, P.Blue);
                DrawText(g, bytes >= 1L << 30 ? $"{Gb(bytes)} GB" : $"{bytes / (1 << 20)} MB", F.BodyBold, P.TextPrimary, x + w, y, right: true);
                y += U(21);
            }
        }
        return y;
    }
}
