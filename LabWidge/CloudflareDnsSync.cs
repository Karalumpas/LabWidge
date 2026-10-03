/// <summary>Only remembers an IP after a completed DNS call. Failures must be retried.</summary>
internal sealed class CloudflareDnsSync
{
    private string? _syncedIp;
    private int _generation;

    public bool NeedsUpdate(string ip) => _syncedIp != ip;

    public void Invalidate()
    {
        _generation++;
        _syncedIp = null;
    }

    public async Task<int> SyncAsync(string ip, Func<Task<int>> update)
    {
        var generation = _generation;
        var changed = await update();
        // A call in progress must not sign off settings saved after the call started.
        if (generation == _generation) _syncedIp = ip;
        return changed;
    }
}
