internal enum AudioKind { Speakers, Headphones, Headset, Display, Digital, Other }

/// <summary>An active audio endpoint. <see cref="Name"/> is a short suggestion; <see cref="FullName"/> is the name Windows uses.
/// <see cref="ContainerId"/> is shared by all parts of the same physical device and is used to find the headset's battery.</summary>
internal sealed record AudioDevice(string Id, string Name, string FullName, AudioKind Kind, Guid? ContainerId)
{
    /// <summary>What the device is, independent of its id: the same device under a new id has the same fingerprint.</summary>
    public string Fingerprint => $"{Kind}|{FullName}";
}

/// <summary>
/// Windows sometimes gives an audio device a new id – the USB dongle in another port, a driver update, Bluetooth paired again.
/// The settings remember the fingerprint of each device they refer to, so a device that comes back under a new id keeps its
/// place in the widget, its name, its microphone and its role as default device.
/// </summary>
internal static class AudioDeviceMemory
{
    /// <summary>Stored instead of the fingerprint for a device that has been seen next to an identical device.</summary>
    private const string Twin = "";

    /// <summary>
    /// Records the fingerprints of the connected devices the settings refer to, and moves the settings from an id that is gone
    /// to the one connected device with the same fingerprint, if that device is not used by the settings already and the old
    /// device has never been seen next to an identical device. True if the settings changed.
    /// </summary>
    public static bool Update(AppSettings s, IReadOnlyList<AudioDevice> outputs, IReadOnlyList<AudioDevice> mics)
    {
        var changed = false;
        foreach (var (devices, output) in new[] { (outputs, true), (mics, false) })
        {
            var used = (output ? OutputIds(s) : MicIds(s)).ToHashSet();
            foreach (var id in used.ToList())
            {
                if (devices.FirstOrDefault(d => d.Id == id) is { } present)
                {
                    // A device with an identical twin (two of the same headset or monitor) can never be told apart from it,
                    // so it is marked for good and not followed – otherwise the twin would take its place when it is unplugged
                    var known = s.AudioDeviceKeys.GetValueOrDefault(id);
                    var key = known == Twin || devices.Count(d => d.Fingerprint == present.Fingerprint) > 1 ? Twin : present.Fingerprint;
                    if (known == key) continue;
                    s.AudioDeviceKeys[id] = key;
                    changed = true;
                    continue;
                }
                if (!s.AudioDeviceKeys.TryGetValue(id, out var fingerprint) || fingerprint == Twin) continue;
                // Only follow a device that can be identified for certain: exactly one connected device with the fingerprint,
                // not used by the settings already. Otherwise the device has a twin, which is remembered, so a twin that is
                // left alone later cannot take over either.
                var matches = devices.Where(d => d.Fingerprint == fingerprint).ToList();
                if (matches.Count == 0) continue;
                if (matches is not [var moved] || used.Contains(moved.Id))
                {
                    s.AudioDeviceKeys[id] = Twin;
                    changed = true;
                    continue;
                }
                Logger.Info($"{moved.FullName} has a new id in Windows – the audio settings follow it.");
                if (output) MoveOutput(s, id, moved.Id);
                else MoveMic(s, id, moved.Id);
                s.AudioDeviceKeys.Remove(id);
                s.AudioDeviceKeys[moved.Id] = fingerprint;
                used.Remove(id);
                used.Add(moved.Id);
                changed = true;
            }
        }
        // Forget the fingerprints of devices the settings no longer refer to
        var all = OutputIds(s).Concat(MicIds(s)).ToHashSet();
        foreach (var id in s.AudioDeviceKeys.Keys.Where(id => !all.Contains(id)).ToList())
        {
            s.AudioDeviceKeys.Remove(id);
            changed = true;
        }
        return changed;
    }

    private static IEnumerable<string> OutputIds(AppSettings s) =>
        (s.AudioDeviceIds ?? Array.Empty<string>()).Concat(s.AudioNames.Keys).Concat(s.AudioMicPairs.Keys)
            .Append(s.AudioStandardId).OfType<string>();

    private static IEnumerable<string> MicIds(AppSettings s) => s.AudioMicPairs.Values.Where(id => id.Length > 0);

    private static void MoveOutput(AppSettings s, string from, string to)
    {
        if (s.AudioDeviceIds != null) s.AudioDeviceIds = s.AudioDeviceIds.Select(id => id == from ? to : id).ToArray();
        if (s.AudioNames.Remove(from, out var name)) s.AudioNames[to] = name;
        if (s.AudioMicPairs.Remove(from, out var mic)) s.AudioMicPairs[to] = mic;
        if (s.AudioStandardId == from) s.AudioStandardId = to;
    }

    private static void MoveMic(AppSettings s, string from, string to)
    {
        foreach (var output in s.AudioMicPairs.Where(p => p.Value == from).Select(p => p.Key).ToList())
            s.AudioMicPairs[output] = to;
    }
}
