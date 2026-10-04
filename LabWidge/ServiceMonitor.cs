using System.Diagnostics;
using System.Net;
using System.Net.Http;

/// <summary>The result of the latest check of one address.</summary>
internal sealed record ServiceStatus(string Host, bool Responded, int? HttpStatus, long? Ms, string? Error, DateTime Checked, int Failures)
{
    /// <summary>Down only after two failures in a row, so a single blip does not cause a false alarm.</summary>
    public bool IsDown => Failures >= 2;

    public string Describe() =>
        Responded ? L.T($"Responds: HTTP {HttpStatus} in {Ms} ms", $"Svarer: HTTP {HttpStatus} på {Ms} ms")
        : HttpStatus is int code ? L.T($"Not responding: HTTP {code}", $"Svarer ikke: HTTP {code}")
                                   + (code >= 520 ? L.T(" from Cloudflare (the service behind the tunnel is not responding)", " fra Cloudflare (tjenesten bag tunnellen svarer ikke)") : "")
        : L.T("Not responding: ", "Svarer ikke: ") + Error;
}

/// <summary>
/// Checks that the services behind the tunnels respond. Only the response headers are fetched (no page content), at most
/// four addresses at a time, and each address is only checked when it is "due" – normally every 5 minutes, but sooner
/// after a failure, so an outage is confirmed and a recovered service is noticed without waiting.
/// </summary>
internal sealed class ServiceMonitor
{
    private static readonly TimeSpan Normal = TimeSpan.FromMinutes(5);
    // Slightly below the timer's 30 seconds, so a failure is confirmed on the next round
    private static readonly TimeSpan AfterFirstFailure = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan WhileDown = TimeSpan.FromMinutes(1);
    private const int Parallel = 4;

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Dictionary<string, ServiceStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Addresses that have responded since the app started. Only these are reported when they go down.</summary>
    private readonly HashSet<string> _seenUp = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedDown = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Updated;
    /// <summary>
    /// A service went down (true) or came back up (false). Services that were already down when the app
    /// started are not reported – otherwise every start would cause a string of messages about the same thing.
    /// </summary>
    public event Action<ServiceStatus, bool>? Changed;

    public ServiceMonitor()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,           // a redirect (e.g. to a login) means the service responds
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.None
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LabWidge-servicecheck");
    }

    public ServiceStatus? Get(string host) => _status.TryGetValue(host, out var s) ? s : null;

    /// <summary>Services that are down right now among the given addresses.</summary>
    public IReadOnlyList<ServiceStatus> Down(IEnumerable<string> hosts) =>
        hosts.Select(Get).OfType<ServiceStatus>().Where(s => s.IsDown).ToList();

    /// <summary>Services that failed once and will be checked again soon.</summary>
    public int Suspect(IEnumerable<string> hosts) =>
        hosts.Select(Get).OfType<ServiceStatus>().Count(s => s.Failures == 1);

    /// <summary>Addresses from the tunnels that can be checked over HTTP(S).</summary>
    public static IReadOnlyList<string> CheckableHosts(IEnumerable<CfTunnel> tunnels, AppSettings settings) =>
        tunnels.SelectMany(t => t.Routes)
            .Where(r => r.Service == null || r.Service.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Hostname)
            .Where(h => !h.Contains('*'))
            .Where(h => !settings.IgnoredServices.Contains(h, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Checks the addresses that are due. Called often (e.g. every 30 seconds); it costs nothing when nothing is due.</summary>
    public async Task CheckDueAsync(IReadOnlyList<string> hosts, CancellationToken cancel = default)
    {
        var now = DateTime.Now;
        var due = hosts.Where(h => IsDue(Get(h), now)).ToList();

        // Addresses that no longer exist are forgotten
        foreach (var gone in _status.Keys.Where(k => !hosts.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            _status.Remove(gone);

        if (due.Count == 0 || !await _running.WaitAsync(0)) return;
        try
        {
            using var gate = new SemaphoreSlim(Parallel);
            var results = await Task.WhenAll(due.Select(async h =>
            {
                await gate.WaitAsync(cancel).ConfigureAwait(false);
                try { return await CheckAsync(h, cancel).ConfigureAwait(false); }
                finally { gate.Release(); }
            }));

            cancel.ThrowIfCancellationRequested();
            foreach (var r in results)
            {
                var before = Get(r.Host);
                var failures = r.Responded ? 0 : (before?.Failures ?? 0) + 1;
                var status = r with { Failures = failures };
                _status[r.Host] = status;

                if (status.Responded) _seenUp.Add(r.Host);
                if (before == null || before.IsDown == status.IsDown) continue;

                if (status.IsDown && _seenUp.Contains(r.Host))
                {
                    _reportedDown.Add(r.Host);
                    Changed?.Invoke(status, true);
                }
                else if (!status.IsDown && _reportedDown.Remove(r.Host))
                {
                    Changed?.Invoke(status, false);
                }
            }
        }
        finally
        {
            _running.Release();
            if (!cancel.IsCancellationRequested) Updated?.Invoke();
        }
    }

    private static bool IsDue(ServiceStatus? s, DateTime now)
    {
        if (s == null) return true;
        var wait = s.IsDown ? WhileDown : s.Failures == 1 ? AfterFirstFailure : Normal;
        return now - s.Checked >= wait;
    }

    private async Task<ServiceStatus> CheckAsync(string host, CancellationToken cancel)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://" + host);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            var code = (int)response.StatusCode;

            // 502-504 and Cloudflare's own 52x codes mean the tunnel cannot reach the service.
            // Everything else – including 401/403/404 and redirects – is an answer from the service itself.
            var responded = code is not (502 or 503 or 504) && code is < 520 or > 530;
            return new ServiceStatus(host, responded, code, sw.ElapsedMilliseconds, null, DateTime.Now, 0);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
        {
            return new ServiceStatus(host, false, null, null, L.T("no answer within 10 s", "intet svar inden for 10 sek."), DateTime.Now, 0);
        }
        catch (HttpRequestException ex)
        {
            return new ServiceStatus(host, false, null, null, ex.InnerException?.Message ?? ex.Message, DateTime.Now, 0);
        }
    }
}
