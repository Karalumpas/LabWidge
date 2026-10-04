using System.Drawing;

// Plugin rendering uses the shared widget drawing context.
internal sealed partial class DashboardForm
{
    internal float DrawSystem(Graphics g, float x, float y, float w)
    {
        var s = _settings;
        var ramFrac = _sys.RamTotal == 0 ? 0 : (double)_sys.RamUsed / _sys.RamTotal;
        var gpu = _sys.Gpu;
        var right = s.CollapsedSystem
            ? $"CPU {_sys.CpuPercent:0} %  ·  RAM {ramFrac * 100:0} %" + (gpu.Available && s.ShowGpu ? $"  ·  GPU {gpu.Percent:0} %" : "")
            : L.T("up ", "tændt i ") + Uptime(_sys.Uptime);
        y = Header(g, "", "SYSTEM", right, s.CollapsedSystem ? _p.TextSecondary : null, x, y, w,
                   s.CollapsedSystem, () => ToggleCollapsed(() => s.CollapsedSystem, v => s.CollapsedSystem = v));
        if (s.CollapsedSystem) return y - U(6);

        var labelW = U(46);
        var valueW = U(112);

        // CPU with sparkline
        DrawText(g, "CPU", _f.Body, _p.TextSecondary, x, y + U(4));
        var cpuColor = _sys.CpuPercent > 90 ? _p.Red : _sys.CpuPercent > 70 ? _p.Amber : _p.TextPrimary;
        DrawText(g, $"{_sys.CpuPercent:0} %", _f.BodyBold, cpuColor, x + w, y + U(4), right: true);
        var spark = new RectangleF(x + labelW, y, w - labelW - U(56), U(24));
        DrawSparkline(g, spark, _sys.CpuHistory, 100, _p.Blue, fill: true);
        _hits.Add(new Hit(new RectangleF(x, y - U(2), w, U(28)), _sys.CpuName + L.T("\nCPU usage over the last minute", "\nCPU-forbrug det seneste minut"), null));
        y += U(32);

        y = BarRow(g, "RAM", ramFrac, $"{Gb(_sys.RamUsed)} / {Gb(_sys.RamTotal)} GB", x, y, w, labelW, valueW,
                   L.T($"Memory: {ramFrac * 100:0} % used\n{Gb(_sys.RamTotal - _sys.RamUsed)} GB free", $"Hukommelse: {ramFrac * 100:0} % brugt\n{Gb(_sys.RamTotal - _sys.RamUsed)} GB fri"), null);

        if (gpu.Available && s.ShowGpu) y = DrawGpu(g, gpu, x, y, w, labelW, valueW);

        foreach (var d in _sys.Disks.Where(d => !s.HiddenDisks.Contains(d.Name, StringComparer.OrdinalIgnoreCase)))
        {
            var frac = d.Total == 0 ? 0 : (double)d.Used / d.Total;
            var name = string.IsNullOrWhiteSpace(d.Label) ? d.Name : $"{d.Name} {d.Label}";
            var drive = d.Name;
            y = BarRow(g, d.Name, frac, $"{Gb(d.Used)} / {Gb(d.Total)} GB", x, y, w, labelW, valueW,
                       name + L.T($"\n{frac * 100:0} % used · {Gb(d.Total - d.Used)} GB free\nClick to open", $"\n{frac * 100:0} % brugt · {Gb(d.Total - d.Used)} GB fri\nKlik for at åbne"), () => OpenDrive(drive));
        }
        return y - U(4);
    }
}
