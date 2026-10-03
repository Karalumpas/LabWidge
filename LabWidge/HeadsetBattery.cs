using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>Battery level in percent and whether the headset is charging.</summary>
internal sealed record BatteryState(int Percent, bool Charging);

/// <summary>The battery colour is stored as "#RRGGBB" in the settings.</summary>
internal static class BatteryColorSetting
{
    public static Color? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            var c = ColorTranslator.FromHtml(hex);
            return c.IsEmpty ? null : Color.FromArgb(c.R, c.G, c.B);
        }
        catch
        {
            return null;
        }
    }

    public static string Format(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>
/// Battery of wireless headsets that Windows does not show itself (USB dongle instead of Bluetooth).
/// Supports Corsair headsets using the "Bragi" protocol (HS80, Virtuoso and others): a 64-byte HID channel
/// on usage page 0xFF42, where property 0x0F is the battery in per mille and 0x10 is the charging state.
/// The HID device is matched to the audio output through the Windows container id, shared by all parts of a USB device.
/// </summary>
internal static class HeadsetBattery
{
    private const ushort CorsairVid = 0x1B1C;
    private const ushort BragiUsagePage = 0xFF42;
    private const byte BragiGet = 0x02;
    private const byte PropBatteryLevel = 0x0F;
    private const byte PropBatteryStatus = 0x10;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(700);

    /// <summary>Reads the battery of the headsets whose container id is in <paramref name="containers"/>.</summary>
    public static Dictionary<Guid, BatteryState> Read(IReadOnlySet<Guid> containers)
    {
        var result = new Dictionary<Guid, BatteryState>();
        if (containers.Count == 0) return result;
        foreach (var (path, container) in BragiInterfaces())
        {
            if (!containers.Contains(container) || result.ContainsKey(container)) continue;
            try
            {
                if (Query(path) is { } state) result[container] = state;
            }
            catch (Exception ex)
            {
                Logger.Error($"Headset battery could not be read: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// Address 0x08 is the device in the USB port: the headset itself over cable, otherwise the dongle, which answers with an
    /// error status. Then the headset behind the dongle is asked on 0x09. A headset that is switched off does not answer at all.
    /// </summary>
    private static BatteryState? Query(string path)
    {
        using var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                      IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        using var stream = new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true);
        foreach (byte device in new byte[] { 0x08, 0x09 })
        {
            var level = Get(stream, device, PropBatteryLevel);
            if (level is not (>= 0 and <= 1000)) continue;
            var status = Get(stream, device, PropBatteryStatus);
            return new BatteryState((int)Math.Round(level.Value / 10.0), status == 1);
        }
        return null;
    }

    private static int? Get(FileStream stream, byte device, byte property)
    {
        var request = new byte[64];
        request[0] = 0x02; // report id for commands
        request[1] = device;
        request[2] = BragiGet;
        request[3] = property;
        if (!stream.WriteAsync(request).AsTask().Wait(Timeout)) return null;

        // iCUE talks on the same channel, so answers that are not a "get" are skipped
        var deadline = DateTime.UtcNow + Timeout;
        var response = new byte[64];
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(deadline - DateTime.UtcNow);
            try
            {
                if (stream.ReadAsync(response, cts.Token).AsTask().GetAwaiter().GetResult() < 6) return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            if (response[0] != 0x01 || response[2] != BragiGet || response[1] != device - 0x08) continue;
            return response[3] == 0 ? response[4] | response[5] << 8 : null;
        }
        return null;
    }

    /// <summary>The paths of Corsair's Bragi command channels and their container id.</summary>
    private static IEnumerable<(string Path, Guid Container)> BragiInterfaces()
    {
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) yield break;
        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
            {
                var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var size, ref info);
                var detail = Marshal.AllocHGlobal((int)size);
                string? path = null;
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, size, out _, ref info))
                        path = Marshal.PtrToStringUni(detail + 4);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
                if (path == null || !path.Contains($"vid_{CorsairVid:x4}", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsBragiCommandChannel(path)) continue;

                var key = DEVPKEY_Device_ContainerId;
                var buffer = new byte[16];
                if (SetupDiGetDeviceProperty(set, ref info, ref key, out _, buffer, (uint)buffer.Length, out _, 0))
                    yield return (path, new Guid(buffer));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static bool IsBragiCommandChannel(string path)
    {
        // No read/write access: opening must not disturb iCUE and does not require the device to be free
        using var handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out var data)) return false;
        try
        {
            return HidP_GetCaps(data, out var caps) == HIDP_STATUS_SUCCESS
                && caps.UsagePage == BragiUsagePage && caps.Usage == 0x01 && caps.OutputReportByteLength == 64;
        }
        finally
        {
            HidD_FreePreparsedData(data);
        }
    }

    // ---------- HID and SetupAPI ----------

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    private static DEVPROPKEY DEVPKEY_Device_ContainerId = new() { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                      NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                      NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
