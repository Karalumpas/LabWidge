using System.Runtime.InteropServices;

internal enum AudioKind { Speakers, Headphones, Headset, Display, Digital, Other }

/// <summary>An active audio endpoint. <see cref="Name"/> is a short suggestion; <see cref="FullName"/> is the name Windows uses.
/// <see cref="ContainerId"/> is shared by all parts of the same physical device and is used to find the headset's battery.</summary>
internal sealed record AudioDevice(string Id, string Name, string FullName, AudioKind Kind, Guid? ContainerId);

/// <summary>Volume 0–1 and whether the device is muted.</summary>
internal sealed record VolumeState(float Level, bool Muted);

/// <summary>
/// Audio endpoints through Windows Core Audio (MMDevice). The default device is switched with IPolicyConfig – the same
/// undocumented but stable interface that the Sound settings, EarTrumpet and SoundSwitch use.
/// </summary>
internal sealed class AudioService
{
    public IReadOnlyList<AudioDevice> Devices { get; private set; } = Array.Empty<AudioDevice>();
    public string? DefaultId { get; private set; }

    /// <summary>Battery per audio output (id) for the headsets where it can be read. Replaced entirely on every refresh.</summary>
    public IReadOnlyDictionary<string, BatteryState> Batteries { get; private set; } = new Dictionary<string, BatteryState>();
    private int _batteryBusy;

    /// <summary>Reads the headset batteries in the background – the HID calls can wait up to a second for an answer.</summary>
    public async Task RefreshBatteriesAsync()
    {
        if (Interlocked.Exchange(ref _batteryBusy, 1) == 1) return;
        try
        {
            var headsets = Devices.Where(d => d.ContainerId != null && d.Kind is AudioKind.Headset or AudioKind.Headphones).ToList();
            var byContainer = await Task.Run(() => HeadsetBattery.Read(headsets.Select(d => d.ContainerId!.Value).ToHashSet()));
            Batteries = headsets.Where(d => byContainer.ContainsKey(d.ContainerId!.Value))
                                .ToDictionary(d => d.Id, d => byContainer[d.ContainerId!.Value]);
        }
        finally
        {
            Interlocked.Exchange(ref _batteryBusy, 0);
        }
    }

    /// <summary>Active microphones (capture devices).</summary>
    public IReadOnlyList<AudioDevice> Microphones { get; private set; } = Array.Empty<AudioDevice>();
    public string? DefaultMicId { get; private set; }

    /// <summary>Volume and mute per device (outputs and microphones), read on every <see cref="Refresh"/>.</summary>
    public IReadOnlyDictionary<string, VolumeState> Volumes { get; private set; } = new Dictionary<string, VolumeState>();

    /// <summary>Reads the devices again. True if the previous default device disappeared (e.g. headphones switched off).</summary>
    public bool Refresh()
    {
        var previousDefault = DefaultId;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                var volumes = new Dictionary<string, VolumeState>();
                Devices = ReadAll(enumerator, EDataFlow.Render, volumes);
                Microphones = ReadAll(enumerator, EDataFlow.Capture, volumes);
                Volumes = volumes;
                DefaultId = DefaultOf(enumerator, EDataFlow.Render, ERole.Multimedia);
                DefaultMicId = DefaultOf(enumerator, EDataFlow.Capture, ERole.Console);
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Audio devices could not be read: {ex.Message}");
            return false;
        }
        return previousDefault != null && Devices.All(d => d.Id != previousDefault);
    }

    private static List<AudioDevice> ReadAll(IMMDeviceEnumerator enumerator, EDataFlow flow, Dictionary<string, VolumeState> volumes)
    {
        var list = new List<AudioDevice>();
        if (enumerator.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out var collection) != 0) return list;
        try
        {
            collection.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0) continue;
                try
                {
                    if (Read(device) is not AudioDevice d) continue;
                    list.Add(d);
                    if (ReadVolume(device) is { } v) volumes[d.Id] = v;
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(collection);
        }
        return list;
    }

    private static string? DefaultOf(IMMDeviceEnumerator enumerator, EDataFlow flow, ERole role)
    {
        if (enumerator.GetDefaultAudioEndpoint(flow, role, out var def) != 0) return null;
        def.GetId(out var id);
        Marshal.ReleaseComObject(def);
        return id;
    }

    /// <summary>
    /// Switches output and – if the user wants it – the matching microphone: the one chosen in the settings,
    /// otherwise the microphone in the same device (the headset's own). See <see cref="PairedMic"/>.
    /// </summary>
    public bool SwitchOutput(string id, AppSettings settings)
    {
        if (!SetDefault(id)) return false;
        if (settings.AudioSwitchMic && PairedMic(id, settings) is { } mic && mic != DefaultMicId)
            SetDefault(mic, capture: true);
        return true;
    }

    /// <summary>
    /// The microphone that goes with the output. Null = leave the microphone alone. Automatic: the output's own microphone
    /// (the headset's), and for outputs without one – e.g. the monitor – a standalone microphone like the webcam,
    /// but only if the current microphone belongs to another output. That way the headset microphone does not linger.
    /// </summary>
    public string? PairedMic(string outputId, AppSettings settings)
    {
        if (settings.AudioMicPairs.TryGetValue(outputId, out var chosen))
            return chosen.Length > 0 && Microphones.Any(m => m.Id == chosen) ? chosen : null;
        if (OwnMic(outputId) is { } own) return own.Id;
        var current = Microphones.FirstOrDefault(m => m.Id == DefaultMicId);
        var currentBelongsToOutput = current?.ContainerId is { } c && c != UnknownContainer && Devices.Any(d => d.ContainerId == c);
        return currentBelongsToOutput ? StandaloneMic()?.Id : null;
    }

    /// <summary>What "Automatic" means for the output – shown in the settings.</summary>
    public AudioDevice? AutomaticMic(string outputId) => OwnMic(outputId) ?? StandaloneMic();

    private AudioDevice? OwnMic(string outputId)
    {
        var output = Devices.FirstOrDefault(d => d.Id == outputId);
        if (output?.ContainerId is not { } container || container == UnknownContainer) return null;
        return Microphones.FirstOrDefault(m => m.ContainerId == container);
    }

    /// <summary>A physical microphone that is not part of one of the outputs – typically the webcam or a desk microphone.</summary>
    private AudioDevice? StandaloneMic() =>
        Microphones.FirstOrDefault(m => m.ContainerId is { } c && c != UnknownContainer && !HiddenByDefault(m)
                                        && Devices.All(d => d.ContainerId != c));

    /// <summary>Devices without a physical device (virtual, built-in) share this container id and must not be paired.</summary>
    private static readonly Guid UnknownContainer = new("00000000-0000-0000-ffff-ffffffffffff");

    /// <summary>Sets the volume (0–1) of an output or microphone.</summary>
    public bool SetVolume(string id, float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        var ok = WithVolume(id, v =>
        {
            var ctx = Guid.Empty;
            return v.SetMasterVolumeLevelScalar(level, ref ctx);
        });
        if (ok) UpdateVolume(id, s => s with { Level = level });
        return ok;
    }

    /// <summary>Mutes or unmutes an output or microphone.</summary>
    public bool SetMute(string id, bool muted)
    {
        var ok = WithVolume(id, v =>
        {
            var ctx = Guid.Empty;
            return v.SetMute(muted, ref ctx);
        });
        if (ok)
        {
            UpdateVolume(id, s => s with { Muted = muted });
            Logger.Info($"{Microphones.FirstOrDefault(m => m.Id == id)?.FullName ?? id} {(muted ? "muted" : "unmuted")}.");
        }
        return ok;
    }

    private void UpdateVolume(string id, Func<VolumeState, VolumeState> change)
    {
        if (!Volumes.TryGetValue(id, out var current)) return;
        Volumes = new Dictionary<string, VolumeState>(Volumes) { [id] = change(current) };
    }

    private static bool WithVolume(string id, Func<IAudioEndpointVolume, int> action)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                if (enumerator.GetDevice(id, out var device) != 0) return false;
                try
                {
                    var iid = IID_IAudioEndpointVolume;
                    if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var instance) != 0) return false;
                    var volume = (IAudioEndpointVolume)instance;
                    try
                    {
                        var hr = action(volume);
                        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                        return true;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(volume);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"The volume could not be changed: {ex.Message}");
            return false;
        }
    }

    private static VolumeState? ReadVolume(IMMDevice device)
    {
        var iid = IID_IAudioEndpointVolume;
        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var instance) != 0) return null;
        var volume = (IAudioEndpointVolume)instance;
        try
        {
            if (volume.GetMasterVolumeLevelScalar(out var level) != 0 || volume.GetMute(out var muted) != 0) return null;
            return new VolumeState(level, muted);
        }
        finally
        {
            Marshal.ReleaseComObject(volume);
        }
    }

    /// <summary>Makes the device the default for both playback and communication (Teams, Discord …).</summary>
    public bool SetDefault(string id, bool capture = false)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                {
                    var hr = policy.SetDefaultEndpoint(id, role);
                    if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
            if (capture) DefaultMicId = id;
            else DefaultId = id;
            Logger.Info($"{(capture ? "Microphone" : "Audio device")} switched to {Devices.Concat(Microphones).FirstOrDefault(d => d.Id == id)?.FullName ?? id}.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not switch audio device: {ex.Message}");
            return false;
        }
    }

    /// <summary>Virtual and duplicate outputs, which are not shown until the user picks them.</summary>
    public static bool HiddenByDefault(AudioDevice d) =>
        new[] { "Steam Streaming", "Virtual", "Hands-Free", "Handsfree" }
            .Any(s => d.FullName.Contains(s, StringComparison.OrdinalIgnoreCase));

    private static AudioDevice? Read(IMMDevice device)
    {
        if (device.GetId(out var id) != 0 || id == null) return null;
        if (device.OpenPropertyStore(STGM_READ, out var store) != 0) return null;
        try
        {
            var desc = ReadString(store, PKEY_Device_DeviceDesc) ?? "";
            var adapter = ReadString(store, PKEY_DeviceInterface_FriendlyName) ?? "";
            var full = ReadString(store, PKEY_Device_FriendlyName) ?? desc;
            var kind = (ReadUInt(store, PKEY_AudioEndpoint_FormFactor) ?? 10) switch
            {
                1 => AudioKind.Speakers,
                3 => AudioKind.Headphones,
                5 => AudioKind.Headset,
                9 => AudioKind.Display,
                2 or 8 => AudioKind.Digital, // line out and S/PDIF
                _ => AudioKind.Other
            };
            // Monitors have a useful name ("C49J89x"); speakers and headsets are called something generic like
            // "Speakers" or "Headset Earphone", so the adapter's name ("CORSAIR HS80 …") says more.
            var name = kind == AudioKind.Display || adapter.Length == 0 ? desc : adapter;
            foreach (var noise in new[] { "(R)", "(TM)", "Wireless Gaming Headset", "High Definition Audio" })
                name = name.Replace(noise, "", StringComparison.OrdinalIgnoreCase);
            name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return new AudioDevice(id, name.Length > 0 ? name : full, full, kind, ReadGuid(store, PKEY_Device_ContainerId));
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try
        {
            return pv.vt == VT_LPWSTR ? Marshal.PtrToStringUni(pv.pointer) : null;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    private static uint? ReadUInt(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try
        {
            return pv.vt == VT_UI4 ? pv.uintValue : null;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    private static Guid? ReadGuid(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try
        {
            return pv.vt == VT_CLSID && pv.pointer != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(pv.pointer) : null;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    // ---------- Core Audio interop ----------

    private const uint DEVICE_STATE_ACTIVE = 1;
    private const uint STGM_READ = 0;
    private const uint CLSCTX_ALL = 23;
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private const ushort VT_LPWSTR = 31;
    private const ushort VT_UI4 = 19;
    private const ushort VT_CLSID = 72;
    private static readonly PropertyKey PKEY_Device_ContainerId = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    private static readonly PropertyKey PKEY_Device_FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PropertyKey PKEY_Device_DeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly PropertyKey PKEY_DeviceInterface_FriendlyName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    private static readonly PropertyKey PKEY_AudioEndpoint_FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

    private enum EDataFlow { Render = 0, Capture = 1 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(Guid f, uint p) { fmtid = f; pid = p; }
    }

    /// <summary>PROPVARIANT is 24 bytes on x64 – the struct must have the full size, or the stack is overwritten.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
        [FieldOffset(8)] public uint uintValue;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    /// <summary>Only the order matters: the unused methods are there so SetDefaultEndpoint lands in the right slot.</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int isDefault, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }

    /// <summary>Only the first methods are used, but the order must match exactly up to GetMute.</summary>
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
