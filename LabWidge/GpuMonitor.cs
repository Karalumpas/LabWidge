using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

/// <summary>
/// Graphics card: NVIDIA is read through NVML (ships with the driver – gives temperature, power and fan).
/// Other cards fall back to Windows' own performance counters (the same source as Task Manager), which only
/// give load and VRAM.
/// </summary>
internal sealed class GpuMonitor
{
    public bool Available { get; private set; }
    public string Name { get; private set; } = "GPU";
    public double Percent { get; private set; }
    public History History { get; } = new(History.FiveMinutes);
    public ulong VramUsed { get; private set; }
    public ulong VramTotal { get; private set; }
    public int? TempC { get; private set; }
    public double? PowerW { get; private set; }
    public double? PowerLimitW { get; private set; }
    public int? FanPercent { get; private set; }
    public int? ClockMhz { get; private set; }
    /// <summary>"NVML" or "Windows" – shown in the tooltip.</summary>
    public string Source { get; private set; } = "";

    private IntPtr _nvDevice;
    private bool _nvTried;
    private PdhGpu? _pdh;
    private bool _pdhTried;

    public void Sample()
    {
        if (!_nvTried)
        {
            _nvTried = true;
            InitNvml();
        }

        if (_nvDevice != IntPtr.Zero)
        {
            SampleNvml();
            return;
        }

        if (!_pdhTried)
        {
            _pdhTried = true;
            _pdh = PdhGpu.TryCreate();
            if (_pdh != null)
            {
                (Name, VramTotal) = ReadLargestAdapter();
                Source = "Windows";
            }
        }
        if (_pdh?.Sample() is var (pct, used) && pct != null)
        {
            Available = true;
            Percent = pct.Value;
            VramUsed = used;
            History.Add(Percent);
        }
    }

    private void InitNvml()
    {
        try
        {
            if (Nvml.nvmlInit_v2() != 0) return;
            if (Nvml.nvmlDeviceGetCount_v2(out var count) != 0 || count == 0) return;
            if (Nvml.nvmlDeviceGetHandleByIndex_v2(0, out var dev) != 0) return;
            _nvDevice = dev;

            var buf = new byte[96];
            if (Nvml.nvmlDeviceGetName(dev, buf, (uint)buf.Length) == 0)
            {
                var name = Encoding.ASCII.GetString(buf).TrimEnd('\0');
                Name = name.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase) ? name[7..] : name;
            }
            if (Nvml.nvmlDeviceGetEnforcedPowerLimit(dev, out var limit) == 0) PowerLimitW = limit / 1000.0;
            Source = "NVML";
        }
        catch
        {
            // nvml.dll does not exist (no NVIDIA card) – use the Windows counters.
            _nvDevice = IntPtr.Zero;
        }
    }

    private void SampleNvml()
    {
        try
        {
            var d = _nvDevice;
            if (Nvml.nvmlDeviceGetUtilizationRates(d, out var util) == 0)
            {
                Percent = Math.Clamp(util.Gpu, 0, 100);
                History.Add(Percent);
                Available = true;
            }
            if (Nvml.nvmlDeviceGetMemoryInfo(d, out var mem) == 0)
            {
                VramTotal = mem.Total;
                VramUsed = mem.Used;
            }
            TempC = Nvml.nvmlDeviceGetTemperature(d, 0, out var t) == 0 ? (int)t : null;
            PowerW = Nvml.nvmlDeviceGetPowerUsage(d, out var mw) == 0 ? mw / 1000.0 : null;
            FanPercent = Nvml.nvmlDeviceGetFanSpeed(d, out var fan) == 0 ? (int)fan : null;
            ClockMhz = Nvml.nvmlDeviceGetClockInfo(d, 0, out var clk) == 0 ? (int)clk : null;
        }
        catch
        {
            Available = false;
        }
    }

    /// <summary>Name and VRAM of the display adapter with the most dedicated memory (the discrete card).</summary>
    private static (string Name, ulong Total) ReadLargestAdapter()
    {
        var best = ("GPU", 0UL);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return best;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    var size = k?.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => (ulong)l,
                        byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
                        _ => 0UL
                    };
                    if (size > best.Item2)
                    {
                        var name = (k?.GetValue("DriverDesc") as string ?? "GPU")
                            .Replace("(R)", "").Replace("(TM)", "").Trim();
                        best = (name, size);
                    }
                }
                catch
                {
                    // No access to this key.
                }
            }
        }
        catch
        {
            // Ignore registry errors.
        }
        return best;
    }

    private static class Nvml
    {
        private const string Dll = "nvml.dll";

        [StructLayout(LayoutKind.Sequential)]
        public struct Utilization { public uint Gpu, Memory; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Memory { public ulong Total, Free, Used; }

        [DllImport(Dll)] public static extern int nvmlInit_v2();
        [DllImport(Dll)] public static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport(Dll)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport(Dll)] public static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
        [DllImport(Dll)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization util);
        [DllImport(Dll)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory mem);
        [DllImport(Dll)] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport(Dll)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport(Dll)] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport(Dll)] public static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
        [DllImport(Dll)] public static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
    }

    /// <summary>
    /// "GPU Engine" and "GPU Adapter Memory" through PDH. Load is computed like Task Manager: per card, each
    /// engine type is summed over all processes, and the busiest engine type is the card's load.
    /// </summary>
    private sealed class PdhGpu
    {
        private readonly IntPtr _query, _engine, _memory;

        private PdhGpu(IntPtr query, IntPtr engine, IntPtr memory)
        {
            _query = query; _engine = engine; _memory = memory;
        }

        public static PdhGpu? TryCreate()
        {
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out var q) != 0) return null;
                if (PdhAddEnglishCounterW(q, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out var eng) != 0
                    || PdhAddEnglishCounterW(q, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out var mem) != 0)
                {
                    PdhCloseQuery(q);
                    return null;
                }
                PdhCollectQueryData(q); // The first sample is the baseline for the percentages.
                return new PdhGpu(q, eng, mem);
            }
            catch
            {
                return null;
            }
        }

        public (double? Percent, ulong VramUsed) Sample()
        {
            if (PdhCollectQueryData(_query) != 0) return (null, 0);

            var memByLuid = new Dictionary<string, double>();
            foreach (var (name, v) in Read(_memory))
                memByLuid[Luid(name)] = memByLuid.GetValueOrDefault(Luid(name)) + v;

            var engines = new Dictionary<(string Luid, string Engine), double>();
            foreach (var (name, v) in Read(_engine))
            {
                var e = name.IndexOf("_engtype_", StringComparison.Ordinal);
                var key = (Luid(name), e >= 0 ? name[(e + 9)..] : "");
                engines[key] = engines.GetValueOrDefault(key) + v;
            }
            if (engines.Count == 0) return (null, 0);

            // The card with the most used VRAM is typically the discrete one that games and programs run on.
            var luid = memByLuid.Count > 0
                ? memByLuid.MaxBy(kv => kv.Value).Key
                : engines.MaxBy(kv => kv.Value).Key.Luid;
            var pct = engines.Where(kv => kv.Key.Luid == luid).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
            return (Math.Clamp(pct, 0, 100), (ulong)memByLuid.GetValueOrDefault(luid));
        }

        private static string Luid(string instance)
        {
            var i = instance.IndexOf("luid_", StringComparison.Ordinal);
            if (i < 0) return instance;
            var end = instance.IndexOf("_phys", i, StringComparison.Ordinal);
            return end > i ? instance[i..end] : instance[i..];
        }

        private static List<(string Name, double Value)> Read(IntPtr counter)
        {
            var result = new List<(string, double)>();
            uint size = 0;
            var status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
            if (status != PDH_MORE_DATA || size == 0) return result;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, buffer) != 0)
                    return result;
                var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                    if (item.CStatus is 0 or 1) // PDH_CSTATUS_VALID_DATA / NEW_DATA
                        result.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return result;
        }

        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_FMT_NOCAP100 = 0x00008000;
        private const uint PDH_MORE_DATA = 0x800007D2;

        [StructLayout(LayoutKind.Sequential)]
        private struct PDH_FMT_COUNTERVALUE_ITEM
        {
            public IntPtr Name;
            public uint CStatus;
            public double Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
    }
}
