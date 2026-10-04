internal sealed record PluginJob(string Name, int IntervalMs, Func<CancellationToken, Task> Run);

/// <summary>Owns polling and cancellation. Reconfiguration drains the previous generation before starting the next.</summary>
internal sealed class PluginRuntime : IDisposable
{
    private readonly List<Worker> _workers = new();
    private readonly Dictionary<string, CancellationTokenSource> _lifetimes = new();
    private Task _draining = Task.CompletedTask;
    private bool _disposed;
    private PluginServices? _services;
    private readonly IReadOnlyList<IWidgetPlugin> _plugins;

    public PluginRuntime(IReadOnlyList<IWidgetPlugin>? plugins = null) => _plugins = plugins ?? WidgetPlugins.All;

    public void ApplySettings(PluginServices services, AppSettings settings)
    {
        if (_disposed) return;
        Stop();
        _services = services;
        foreach (var plugin in _plugins.Where(p => settings.IsPluginEnabled(p.Key)))
        {
            var lifetime = new CancellationTokenSource();
            _lifetimes.Add(plugin.Key, lifetime);
            if (plugin.Key == "ha") services.HomeAssistant.Lifetime = lifetime.Token;
            if (plugin.Key == "proxmox") services.Proxmox.Lifetime = lifetime.Token;
            foreach (var job in plugin.CreateJobs(services, settings))
                _workers.Add(new Worker(plugin.Key, job, lifetime.Token, _draining));
        }
    }

    public CancellationToken TokenFor(string key) => _lifetimes.TryGetValue(key, out var lifetime)
        ? lifetime.Token : new CancellationToken(canceled: true);

    public void Refresh(string key)
    {
        foreach (var worker in _workers.Where(w => w.Key == key)) worker.Refresh();
    }

    public void Stop()
    {
        var lifetimes = _lifetimes.Values.ToArray();
        foreach (var lifetime in lifetimes) lifetime.Cancel();
        foreach (var worker in _workers) worker.Stop();
        _draining = Task.WhenAll(_workers.Select(w => w.Completion).Append(_draining));
        _ = DisposeLifetimesAsync(lifetimes, _draining);
        _lifetimes.Clear();
        _workers.Clear();
        _services?.System.Suspend();
        _services?.Network.Suspend();
    }

    public void Dispose() { _disposed = true; Stop(); }

    private static async Task DisposeLifetimesAsync(CancellationTokenSource[] lifetimes, Task draining)
    {
        await draining;
        foreach (var lifetime in lifetimes) lifetime.Dispose();
    }

    private sealed class Worker
    {
        private readonly string _key;
        private readonly PluginJob _job;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly CancellationToken _cancel;
        private bool _stopped;
        public string Key => _key;
        public Task Completion { get; private set; } = Task.CompletedTask;

        public Worker(string key, PluginJob job, CancellationToken cancel, Task previous)
        {
            _key = key; _job = job; _cancel = cancel;
            _timer = new() { Interval = Math.Max(100, job.IntervalMs) };
            _timer.Tick += (_, _) => Refresh();
            Completion = StartAsync(previous);
        }

        private async Task StartAsync(Task previous)
        {
            await previous;
            if (_stopped) return;
            _timer.Start();
            await RunAsync();
        }

        private async Task RunAsync()
        {
            try { _cancel.ThrowIfCancellationRequested(); await _job.Run(_cancel); }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
            catch (Exception ex) { Logger.Error($"Plugin {_key} ({_job.Name}): {ex.Message}"); }
        }

        public void Refresh() { if (Completion.IsCompleted && !_stopped) Completion = RunAsync(); }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            _timer.Stop(); _timer.Dispose();
        }

    }
}
