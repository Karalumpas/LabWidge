using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

/// <summary>Fixed-size ring buffer for sparklines.</summary>
internal sealed class History
{
    private readonly double[] _values;
    private int _start;
    public int Count { get; private set; }

    public History(int capacity) => _values = new double[capacity];

    public void Clear() { _start = 0; Count = 0; }

    public void Add(double v)
    {
        if (Count < _values.Length)
        {
            _values[(_start + Count) % _values.Length] = v;
            Count++;
        }
        else
        {
            _values[_start] = v;
            _start = (_start + 1) % _values.Length;
        }
    }

    public int Capacity => _values.Length;
    public double this[int i] => _values[(_start + i) % _values.Length];
    public double Max() { double m = 0; for (var i = 0; i < Count; i++) m = Math.Max(m, this[i]); return m; }

    /// <summary>The newest values, oldest first – at most <paramref name="n"/>.</summary>
    public List<double> Tail(int n)
    {
        var take = Math.Min(n, Count);
        var list = new List<double>(take);
        for (var i = Count - take; i < Count; i++) list.Add(this[i]);
        return list;
    }

    /// <summary>Five minutes of one-second samples – the widget shows the last minute, the section windows all of it.</summary>
    public const int FiveMinutes = 300;
}

internal sealed record DiskInfo(string Name, string Label, long Used, long Total);

internal sealed class SystemMonitor
{
    public string CpuName { get; } = ReadCpuName();
    public double CpuPercent { get; private set; }
    public History CpuHistory { get; } = new(History.FiveMinutes);
    /// <summary>RAM in use, in percent.</summary>
    public History RamHistory { get; } = new(History.FiveMinutes);
    public ulong RamUsed { get; private set; }
    public ulong RamTotal { get; private set; }
    public IReadOnlyList<DiskInfo> Disks { get; private set; } = Array.Empty<DiskInfo>();
    public TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);
    public GpuMonitor Gpu { get; } = new();

    private ulong _prevIdle, _prevKernel, _prevUser;
    private DateTime _lastDiskRead = DateTime.MinValue;
    public void Suspend() { _prevIdle = _prevKernel = _prevUser = 0; Gpu.Suspend(); }

    public void Sample(bool includeGpu = true)
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong i = idle.Value, k = kernel.Value, u = user.Value;
            if (_prevKernel != 0)
            {
                var total = (k - _prevKernel) + (u - _prevUser); // kernel includes idle
                var busy = total - (i - _prevIdle);
                CpuPercent = total == 0 ? 0 : Math.Clamp(busy * 100.0 / total, 0, 100);
                CpuHistory.Add(CpuPercent);
            }
            _prevIdle = i; _prevKernel = k; _prevUser = u;
        }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            RamTotal = mem.ullTotalPhys;
            RamUsed = mem.ullTotalPhys - mem.ullAvailPhys;
            RamHistory.Add(RamTotal == 0 ? 0 : RamUsed * 100.0 / RamTotal);
        }

        if (includeGpu) Gpu.Sample();
        else Gpu.Suspend();

        if ((DateTime.Now - _lastDiskRead).TotalSeconds >= 30)
        {
            _lastDiskRead = DateTime.Now;
            try
            {
                Disks = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                    .Select(d => new DiskInfo(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize - d.TotalFreeSpace, d.TotalSize))
                    .ToList();
            }
            catch
            {
                // Ignore drive errors.
            }
        }
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
            foreach (var noise in new[] { "(R)", "(TM)", "CPU", "Processor", "with Radeon Graphics" })
                name = name.Replace(noise, "", StringComparison.OrdinalIgnoreCase);
            var at = name.IndexOf('@');
            if (at > 0) name = name[..at];
            return string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        catch
        {
            return "CPU";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME64 { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME64 idle, out FILETIME64 kernel, out FILETIME64 user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}

internal sealed record AdapterInfo(string Name, string Kind, string Ipv4, int PrefixLength, string? Gateway, string[] Dns, long SpeedBps, bool IsPrimary);

internal sealed class NetworkMonitor
{
    /// <summary>Kind of adapter for VPNs, Hyper-V, WSL and other virtual adapters.</summary>
    public const string VirtualKind = "Virtual";

    public IReadOnlyList<AdapterInfo> Adapters { get; private set; } = Array.Empty<AdapterInfo>();
    public double DownBps { get; private set; }
    public double UpBps { get; private set; }
    public History DownHistory { get; } = new(History.FiveMinutes);
    public History UpHistory { get; } = new(History.FiveMinutes);
    /// <summary>Ping every 5 seconds for 10 minutes; -1 when there was no answer.</summary>
    public History PingHistory { get; } = new(120);
    public long? PingMs { get; private set; }
    /// <summary>The host the latency is measured against; set from the settings.</summary>
    public string PingTarget
    {
        get => _pingTarget;
        set
        {
            var target = string.IsNullOrWhiteSpace(value) ? "1.1.1.1" : value.Trim();
            if (target == _pingTarget) return;
            _pingTarget = target;
            PingHistory.Clear(); // the old numbers belong to another host
            _lastPing = DateTime.MinValue;
        }
    }
    private string _pingTarget = "1.1.1.1";

    private long _prevRx, _prevTx;
    private DateTime _prevSample = DateTime.MinValue;
    private DateTime _lastAdapterRead = DateTime.MinValue;
    private DateTime _lastPing = DateTime.MinValue;
    private bool _pinging;

    private bool _subscribed;
    private void OnNetworkChanged(object? sender, EventArgs e) => _lastAdapterRead = DateTime.MinValue;

    public void Suspend()
    {
        if (_subscribed) NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _subscribed = false;
        _prevSample = DateTime.MinValue;
        _lastPing = _lastAdapterRead = DateTime.MinValue;
    }

    public void Sample(CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        if (!_subscribed)
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            _subscribed = true;
        }
        var now = DateTime.Now;
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToArray();
        }
        catch
        {
            return;
        }

        long rx = 0, tx = 0;
        foreach (var n in nics)
        {
            try
            {
                var st = n.GetIPStatistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
            catch
            {
                // Ignore adapters without statistics.
            }
        }

        if (_prevSample != DateTime.MinValue)
        {
            var secs = Math.Max(0.2, (now - _prevSample).TotalSeconds);
            DownBps = Math.Max(0, (rx - _prevRx) * 8 / secs);
            UpBps = Math.Max(0, (tx - _prevTx) * 8 / secs);
            DownHistory.Add(DownBps);
            UpHistory.Add(UpBps);
        }
        _prevRx = rx; _prevTx = tx; _prevSample = now;

        if ((now - _lastAdapterRead).TotalSeconds >= 15)
        {
            _lastAdapterRead = now;
            Adapters = ReadAdapters(nics);
        }

        if (!_pinging && (now - _lastPing).TotalSeconds >= 5)
        {
            _lastPing = now;
            _ = PingAsync(cancel);
        }
    }

    private async Task PingAsync(CancellationToken cancel)
    {
        _pinging = true;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(PingTarget, TimeSpan.FromSeconds(2), cancellationToken: cancel);
            PingMs = reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch
        {
            PingMs = null;
        }
        finally
        {
            if (!cancel.IsCancellationRequested) PingHistory.Add(PingMs ?? -1);
            _pinging = false;
        }
    }

    private static IReadOnlyList<AdapterInfo> ReadAdapters(NetworkInterface[] nics)
    {
        var list = new List<AdapterInfo>();
        foreach (var n in nics)
        {
            try
            {
                var props = n.GetIPProperties();
                var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 == null) continue;

                var gw = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any));
                var dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToArray();
                var kind = n.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                    _ => n.NetworkInterfaceType.ToString()
                };
                if (n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    || n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                    || n.Description.Contains("WSL", StringComparison.OrdinalIgnoreCase)
                    || n.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase))
                {
                    kind = VirtualKind;
                }

                list.Add(new AdapterInfo(n.Name, kind, v4.Address.ToString(), v4.PrefixLength, gw?.ToString(), dns, n.Speed, gw != null));
            }
            catch
            {
                // Ignore adapter read errors.
            }
        }

        // Primary ones (with a gateway, not virtual) first
        return list.OrderByDescending(a => a.IsPrimary && a.Kind != VirtualKind)
                   .ThenByDescending(a => a.IsPrimary)
                   .ThenBy(a => a.Kind == VirtualKind)
                   .ToList();
    }
}
