using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

/// <summary>A public address that a tunnel forwards to a service on the home network.</summary>
internal sealed record CfRoute(string Hostname, string? Service);

/// <summary>A Cloudflare Tunnel (cloudflared) with status and active connections.</summary>
internal sealed record CfTunnel(
    string Id, string Name, string Status, int Connections, string[] Colos,
    string? ClientVersion, string? OriginIp, DateTime? ActiveSince, IReadOnlyList<CfRoute> Routes)
{
    public bool IsHealthy => Status == "healthy";
    public bool IsDown => Status is "down" or "inactive";

    public string StatusText => Status switch
    {
        "healthy" => L.T("up", "oppe"),
        "degraded" => L.T("partly down", "delvist nede"),
        "down" => L.T("down", "nede"),
        "inactive" => L.T("no connection", "ingen forbindelse"),
        _ => Status
    };
}

/// <summary>
/// Fetches tunnels and DNS records from Cloudflare for the widget's Cloudflare section. Tunnels require
/// an Account ID and the permission Account → Cloudflare Tunnel → Read; without them only DNS is shown.
/// </summary>
internal sealed class CloudflareService
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _zoneLookedUp;
    private string? _zoneAccountId;
    private string? _zoneErrorLogged;

    public IReadOnlyList<CfTunnel> Tunnels { get; private set; } = Array.Empty<CfTunnel>();
    public IReadOnlyList<CloudflareRecord> ARecords { get; private set; } = Array.Empty<CloudflareRecord>();
    public string? ZoneName { get; private set; }
    public string? AccountId { get; private set; }
    public string? DnsError { get; private set; }
    /// <summary>Set when the tunnels cannot be shown because no Account ID is known.</summary>
    public const string MissingAccountId = "Account ID missing";

    /// <summary>Why tunnels cannot be shown. Null once they are fetched.</summary>
    public string? TunnelError { get; private set; }
    public DateTime LastFetch { get; private set; } = DateTime.MinValue;
    public DateTime LastSuccessfulDnsFetch { get; private set; } = DateTime.MinValue;

    public event Action? Updated;

    public CloudflareService(HttpClient http) => _http = http;

    public async Task RefreshAsync(AppSettings settings, CancellationToken cancel = default)
    {
        if (!settings.HasCloudflare)
        {
            if (Tunnels.Count > 0 || ARecords.Count > 0 || DnsError != null)
            {
                Tunnels = Array.Empty<CfTunnel>();
                ARecords = Array.Empty<CloudflareRecord>();
                DnsError = TunnelError = null;
                if (!cancel.IsCancellationRequested) Updated?.Invoke();
            }
            return;
        }

        if (!await _lock.WaitAsync(0)) return;
        try
        {
            var token = CredentialStore.ReadToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                DnsError = TunnelError = L.T("No token saved", "Intet token gemt");
                return;
            }

            var zoneId = settings.ZoneId!.Trim();
            await LoadZoneAsync(zoneId, token, cancel);
            AccountId = settings.CloudflareAccountId is { Length: > 0 } acc ? acc.Trim() : _zoneAccountId;

            CloudflareRecord[] cnames = Array.Empty<CloudflareRecord>();
            try
            {
                ARecords = await CloudflareClient.GetRecordsAsync(zoneId, token, "A", cancel: cancel);
                cnames = await CloudflareClient.GetRecordsAsync(zoneId, token, "CNAME", cancel: cancel);
                DnsError = null;
                LastSuccessfulDnsFetch = DateTime.Now;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var error = Describe(ex);
                if (error != DnsError) Logger.Error($"Cloudflare DNS could not be fetched: {ex.Message}");
                DnsError = error;
            }

            if (string.IsNullOrWhiteSpace(AccountId))
            {
                TunnelError = MissingAccountId;
            }
            else
            {
                try
                {
                    Tunnels = await LoadTunnelsAsync(AccountId, token, cnames, cancel);
                    TunnelError = null;
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var error = ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized }
                        ? L.T("The token lacks the permission Cloudflare Tunnel: Read", "Tokenet mangler rettigheden Cloudflare Tunnel: Read")
                        : Describe(ex);
                    if (error != TunnelError) Logger.Error($"Cloudflare tunnels could not be fetched: {ex.Message}");
                    TunnelError = error;
                    // A brief network outage must not make the tunnels look down
                    if (ex is not (HttpRequestException { StatusCode: null } or TaskCanceledException)) Tunnels = Array.Empty<CfTunnel>();
                }
            }
            LastFetch = DateTime.Now;
        }
        finally
        {
            _lock.Release();
            if (!cancel.IsCancellationRequested) Updated?.Invoke();
        }
    }

    /// <summary>The zone's name and account are used for links to the dashboard. Requires Zone → Read; without it we do without.</summary>
    private async Task LoadZoneAsync(string zoneId, string token, CancellationToken cancel)
    {
        if (_zoneLookedUp == zoneId) return;
        ZoneName = null;
        _zoneAccountId = null;
        try
        {
            using var doc = await GetAsync($"zones/{zoneId}", token, cancel);
            var result = doc.RootElement.GetProperty("result");
            ZoneName = Str(result, "name");
            if (result.TryGetProperty("account", out var account)) _zoneAccountId = Str(account, "id");
            _zoneLookedUp = zoneId;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (_zoneErrorLogged != ex.Message) Logger.Info($"The Cloudflare zone could not be looked up ({ex.Message}) – links go to the front page.");
            _zoneErrorLogged = ex.Message;
        }
    }

    private async Task<IReadOnlyList<CfTunnel>> LoadTunnelsAsync(string accountId, string token, CloudflareRecord[] cnames, CancellationToken cancel)
    {
        using var doc = await GetAsync($"accounts/{accountId}/cfd_tunnel?is_deleted=false&per_page=50", token, cancel);
        var list = new List<CfTunnel>();
        foreach (var t in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var id = Str(t, "id") ?? "";
            var connections = t.TryGetProperty("connections", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Where(x => !(x.TryGetProperty("is_pending_reconnect", out var p) && p.ValueKind == JsonValueKind.True)).ToList()
                : new List<JsonElement>();

            // Addresses configured in the dashboard, plus CNAME records pointing to the tunnel
            var routes = new List<CfRoute>();
            if (t.TryGetProperty("remote_config", out var rc) && rc.ValueKind == JsonValueKind.True)
            {
                routes.AddRange(await LoadIngressAsync(accountId, id, token, cancel));
            }
            foreach (var cname in cnames.Where(r => r.Content.Equals($"{id}.cfargotunnel.com", StringComparison.OrdinalIgnoreCase)))
            {
                if (!routes.Any(r => r.Hostname.Equals(cname.Name, StringComparison.OrdinalIgnoreCase)))
                    routes.Add(new CfRoute(cname.Name, null));
            }

            DateTime? since = t.TryGetProperty("conns_active_at", out var at) && at.ValueKind == JsonValueKind.String
                              && at.TryGetDateTime(out var dt) ? dt.ToLocalTime() : null;

            list.Add(new CfTunnel(
                id,
                Str(t, "name") ?? id,
                Str(t, "status") ?? "inactive",
                connections.Count,
                connections.Select(x => Str(x, "colo_name")).OfType<string>().Distinct().ToArray(),
                connections.Select(x => Str(x, "client_version")).OfType<string>().FirstOrDefault(),
                connections.Select(x => Str(x, "origin_ip")).OfType<string>().FirstOrDefault(),
                since,
                routes.OrderBy(r => r.Hostname, StringComparer.OrdinalIgnoreCase).ToList()));
        }
        return list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<IEnumerable<CfRoute>> LoadIngressAsync(string accountId, string tunnelId, string token, CancellationToken cancel)
    {
        try
        {
            using var doc = await GetAsync($"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations", token, cancel);
            if (!doc.RootElement.GetProperty("result").TryGetProperty("config", out var config)
                || config.ValueKind != JsonValueKind.Object
                || !config.TryGetProperty("ingress", out var ingress))
            {
                return Array.Empty<CfRoute>();
            }

            // The last rule is typically "everything else → 404" without a hostname; it is not an address
            return ingress.EnumerateArray()
                .Select(r => (Host: Str(r, "hostname"), Path: Str(r, "path"), Service: Str(r, "service")))
                .Where(r => !string.IsNullOrWhiteSpace(r.Host))
                .Select(r => new CfRoute(r.Path is { Length: > 0 } p ? $"{r.Host}/{p.TrimStart('/')}" : r.Host!, r.Service))
                .ToList();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Logger.Info($"Tunnel configuration for {tunnelId} could not be fetched: {ex.Message}");
            return Array.Empty<CfRoute>();
        }
    }

    /// <summary>A records the widget keeps updated – all of them, or only the ones the user chose.</summary>
    public IReadOnlyList<CloudflareRecord> ManagedRecords(AppSettings settings)
    {
        if (settings.UpdateAllARecords) return ARecords;
        return ARecords.Where(r => settings.IncludedHosts.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public string DashboardUrl(string section = "")
    {
        if (AccountId == null) return "https://dash.cloudflare.com/";
        if (ZoneName == null || section.Length == 0) return $"https://dash.cloudflare.com/{AccountId}";
        return $"https://dash.cloudflare.com/{AccountId}/{ZoneName}/{section}";
    }

    public string TunnelsUrl() =>
        AccountId == null ? "https://one.dash.cloudflare.com/" : $"https://one.dash.cloudflare.com/{AccountId}/networks/tunnels";

    private async Task<JsonDocument> GetAsync(string path, string token, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.cloudflare.com/client/v4/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancel).ConfigureAwait(true);
        var json = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(FirstError(json) ?? $"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }
        return JsonDocument.Parse(json);
    }

    private static string? FirstError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0
                ? Str(errors[0], "message")
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => L.T("The token has no access", "Tokenet har ikke adgang"),
        HttpRequestException { StatusCode: null } or TaskCanceledException => L.T("No connection to Cloudflare", "Ingen forbindelse til Cloudflare"),
        _ => ex.Message
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
