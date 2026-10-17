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
    /// <summary>Put in front of the fingerprint of a device that has been seen with an identical device (two of the same
    /// headset or monitor). Such a device can never be told apart from its twin, so it is not followed to a new id.</summary>
    private const string TwinMark = "twin:";

    /// <summary>
    /// Records the fingerprints of the connected devices the settings refer to, and moves the settings from an id that is gone
    /// to the one connected device with the same fingerprint – but only if no other device the settings use has that
    /// fingerprint, and the old device has never been seen with an identical device. True if the settings changed.
    /// </summary>
    public static bool Update(AppSettings s, IReadOnlyList<AudioDevice> outputs, IReadOnlyList<AudioDevice> mics)
    {
        var changed = false;
        var stillUsed = OutputIds(s).Concat(MicIds(s)).ToHashSet();
        var removedKeys = s.AudioDeviceKeys.Where(k => !stillUsed.Contains(k.Key)).ToList();
        foreach (var (devices, output) in new[] { (outputs, true), (mics, false) })
        {
            var used = (output ? OutputIds(s) : MicIds(s)).ToHashSet();
            // The other devices the settings use that have the fingerprint – connected or not, marked as twins or not
            List<string> ChosenTwins(string id, string fingerprint) =>
                used.Where(u => u != id && s.AudioDeviceKeys.TryGetValue(u, out var k) && FingerprintOf(k) == fingerprint).ToList();
            void MarkTwins(IEnumerable<string> ids, string fingerprint)
            {
                foreach (var twin in ids.Where(t => s.AudioDeviceKeys.GetValueOrDefault(t) != TwinMark + fingerprint).ToList())
                {
                    s.AudioDeviceKeys[twin] = TwinMark + fingerprint;
                    changed = true;
                }
            }

            // A device just removed from the settings is forgotten below – but a chosen device with the same fingerprint is
            // a twin of it, and must not be followed to the removed device when that comes back under a new id
            foreach (var (removed, key) in removedKeys)
                MarkTwins(ChosenTwins(removed, FingerprintOf(key)), FingerprintOf(key));

            foreach (var id in used.ToList())
            {
                var known = s.AudioDeviceKeys.GetValueOrDefault(id);
                if (devices.FirstOrDefault(d => d.Id == id) is { } present)
                {
                    var fingerprint = present.Fingerprint;
                    var twins = ChosenTwins(id, fingerprint)
                        .Concat(devices.Where(d => d.Id != id && d.Fingerprint == fingerprint).Select(d => d.Id).Where(used.Contains)).ToList();
                    if (known?.StartsWith(TwinMark) == true || twins.Count > 0 || devices.Count(d => d.Fingerprint == fingerprint) > 1)
                    {
                        MarkTwins(twins.Append(id), fingerprint);
                        continue;
                    }
                    if (known == fingerprint) continue;
                    s.AudioDeviceKeys[id] = fingerprint;
                    changed = true;
                    continue;
                }
                if (known == null || known.StartsWith(TwinMark)) continue;
                // Only follow a device that can be identified for certain: exactly one connected device with the fingerprint,
                // and no other chosen device with it. Otherwise all of them are marked as twins, so one of them that is left
                // alone later cannot take over another's settings either.
                var matches = devices.Where(d => d.Fingerprint == known).ToList();
                if (matches.Count == 0) continue;
                var others = ChosenTwins(id, known).Concat(matches.Select(d => d.Id).Where(used.Contains)).ToList();
                if (matches is not [var moved] || others.Count > 0)
                {
                    MarkTwins(others.Append(id), known);
                    continue;
                }
                Logger.Info($"{moved.FullName} has a new id in Windows – the audio settings follow it.");
                if (output) MoveOutput(s, id, moved.Id);
                else MoveMic(s, id, moved.Id);
                s.AudioDeviceKeys.Remove(id);
                s.AudioDeviceKeys[moved.Id] = known;
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

    private static string FingerprintOf(string key) => key.StartsWith(TwinMark) ? key[TwinMark.Length..] : key;

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
