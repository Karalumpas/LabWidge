using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

/// <summary>A Proxmox node (physical server).</summary>
internal sealed record PveNode(string Name, bool Online, double Cpu, int MaxCpu, long Mem, long MaxMem, long Disk, long MaxDisk, TimeSpan Uptime);

/// <summary>A virtual machine (qemu) or container (lxc).</summary>
internal sealed record PveGuest(int VmId, string Name, string Type, string Node, string Status, double Cpu, int MaxCpu, long Mem, long MaxMem, TimeSpan Uptime)
{
    public bool IsRunning => Status == "running";
    public string Kind => Type == "lxc" ? "CT" : "VM";
}

/// <summary>A storage on a node. Shared storages are shown once.</summary>
internal sealed record PveStorage(string Name, string Node, long Used, long Total, bool Shared);

/// <summary>
/// Reads the status from Proxmox VE with one call to /cluster/resources and can start, shut down and reboot machines.
/// Proxmox usually uses a self-signed certificate; it is accepted if the user allowed it, and the fingerprint
/// is remembered so a different certificate is refused later (trust on first use).
/// </summary>
internal sealed class ProxmoxService
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AppSettings _settings = new();

    public IReadOnlyList<PveNode> Nodes { get; private set; } = Array.Empty<PveNode>();
    public IReadOnlyList<PveGuest> Guests { get; private set; } = Array.Empty<PveGuest>();
    public IReadOnlyList<PveStorage> Storages { get; private set; } = Array.Empty<PveStorage>();
    public string? LastError { get; private set; }
    public DateTime LastFetch { get; private set; } = DateTime.MinValue;

    private sealed record Pending(string Action, string FromStatus, DateTime Since);
    private readonly Dictionary<int, Pending> _pending = new();

    /// <summary>The action sent to the machine that has not taken effect yet.</summary>
    public string? PendingAction(int vmid) => _pending.TryGetValue(vmid, out var p) ? p.Action : null;

    public event Action? Updated;
    /// <summary>A new certificate fingerprint was remembered and must be saved in the settings.</summary>
    public event Action? CertificatePinned;

    public ProxmoxService()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = ValidateCertificate }
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private bool ValidateCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (!_settings.ProxmoxAllowSelfSigned || cert == null) return false;

        var thumbprint = new X509Certificate2(cert).GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        if (string.IsNullOrEmpty(_settings.ProxmoxCertThumbprint))
        {
            _settings.ProxmoxCertThumbprint = thumbprint;
            Logger.Info($"Proxmox certificate remembered: {thumbprint}");
            CertificatePinned?.Invoke();
            return true;
        }
        if (string.Equals(_settings.ProxmoxCertThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)) return true;

        Logger.Error($"The Proxmox certificate changed ({thumbprint}) – the connection is refused.");
        return false;
    }

    public CancellationToken Lifetime { get; set; }

    public async Task RefreshAsync(AppSettings settings, CancellationToken cancel = default)
    {
        _settings = settings;
        if (!settings.HasProxmox)
        {
            if (Nodes.Count > 0 || Guests.Count > 0 || LastError != null)
            {
                Nodes = Array.Empty<PveNode>();
                Guests = Array.Empty<PveGuest>();
                Storages = Array.Empty<PveStorage>();
                LastError = null;
                if (!cancel.IsCancellationRequested) Updated?.Invoke();
            }
            return;
        }

        if (!await _lock.WaitAsync(0)) return;
        try
        {
            using var doc = await SendAsync(HttpMethod.Get, "cluster/resources", cancel: cancel);
            var nodes = new List<PveNode>();
            var guests = new List<PveGuest>();
            var storages = new List<PveStorage>();

            foreach (var r in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                switch (Str(r, "type"))
                {
                    case "node":
                        nodes.Add(new PveNode(Str(r, "node") ?? "?", Str(r, "status") == "online",
                            Num(r, "cpu"), (int)Num(r, "maxcpu"), Long(r, "mem"), Long(r, "maxmem"),
                            Long(r, "disk"), Long(r, "maxdisk"), TimeSpan.FromSeconds(Num(r, "uptime"))));
                        break;
                    case "qemu":
                    case "lxc":
                        if (Num(r, "template") == 1) break;
                        guests.Add(new PveGuest((int)Num(r, "vmid"), Str(r, "name") ?? $"#{Num(r, "vmid")}", Str(r, "type")!,
                            Str(r, "node") ?? "", Str(r, "status") ?? "unknown", Num(r, "cpu"), (int)Num(r, "maxcpu"),
                            Long(r, "mem"), Long(r, "maxmem"), TimeSpan.FromSeconds(Num(r, "uptime"))));
                        break;
                    case "storage":
                        if (Long(r, "maxdisk") <= 0 || Str(r, "status") != "available") break;
                        var shared = Num(r, "shared") == 1;
                        var name = Str(r, "storage") ?? "?";
                        if (shared && storages.Any(s => s.Shared && s.Name == name)) break;
                        storages.Add(new PveStorage(name, Str(r, "node") ?? "", Long(r, "disk"), Long(r, "maxdisk"), shared));
                        break;
                }
            }

            cancel.ThrowIfCancellationRequested();
            Nodes = nodes.OrderBy(n => n.Name).ToList();
            Guests = guests.OrderBy(g => g.VmId).ToList();
            Storages = storages.OrderByDescending(s => s.Total).ToList();
            LastError = null;
            LastFetch = DateTime.Now;

            // An action has taken effect when the status changed – or it is given up after two minutes
            foreach (var (vmid, p) in _pending.ToList())
            {
                var guest = Guests.FirstOrDefault(g => g.VmId == vmid);
                var done = guest == null || guest.Status != p.FromStatus || (p.Action == "reboot" && guest.Uptime.TotalSeconds < 60);
                if (done || DateTime.Now - p.Since > TimeSpan.FromMinutes(2)) _pending.Remove(vmid);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var error = Describe(ex);
            if (error != LastError) Logger.Error($"Proxmox: {ex.Message}");
            LastError = error;
        }
        finally
        {
            _lock.Release();
            if (!cancel.IsCancellationRequested) Updated?.Invoke();
        }
    }

    /// <summary>Sends start, shutdown or reboot. Requires the permission VM.PowerMgmt.</summary>
    public async Task<string?> PowerAsync(PveGuest guest, string action)
    {
        var cancel = Lifetime;
        if (cancel.IsCancellationRequested) return L.T("Plugin is disabled", "Pluginet er deaktiveret");
        try
        {
            using var _ = await SendAsync(HttpMethod.Post, $"nodes/{guest.Node}/{guest.Type}/{guest.VmId}/status/{action}", cancel: cancel);
            _pending[guest.VmId] = new Pending(action, guest.Status, DateTime.Now);
            Logger.Info($"Proxmox: {action} sent to {guest.Kind} {guest.VmId} ({guest.Name}).");
            Updated?.Invoke();
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"Proxmox: {action} of {guest.VmId} failed: {ex.Message}");
            return ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden }
                ? L.T("The token lacks the permission VM.PowerMgmt", "Tokenet har ikke rettigheden VM.PowerMgmt")
                : Describe(ex);
        }
    }

    public string WebUrl(PveGuest? guest = null)
    {
        var baseUrl = Normalize(_settings.ProxmoxUrl ?? "");
        return guest == null ? baseUrl : $"{baseUrl}/#v1:0:={guest.Type}%2F{guest.VmId}";
    }

    /// <summary>Test from the settings: returns a description of what was found.</summary>
    public async Task<string> TestAsync(AppSettings settings, string secret)
    {
        _settings = settings;
        using var doc = await SendAsync(HttpMethod.Get, "cluster/resources", secret);
        var items = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        var nodes = items.Count(i => Str(i, "type") == "node");
        var guests = items.Count(i => Str(i, "type") is "qemu" or "lxc");
        return L.T($"{nodes} node{(nodes == 1 ? "" : "s")}, {guests} VMs and containers", $"{nodes} node{(nodes == 1 ? "" : "s")}, {guests} VM'er og containere");
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? secret = null, CancellationToken cancel = default)
    {
        secret ??= CredentialStore.ReadProxmoxSecret();
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException(L.T("No token saved", "Intet token gemt"));

        using var request = new HttpRequestMessage(method, $"{Normalize(_settings.ProxmoxUrl!)}/api2/json/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("PVEAPIToken", $"{_settings.ProxmoxTokenId!.Trim()}={secret.Trim()}");
        using var response = await _http.SendAsync(request, cancel).ConfigureAwait(true);
        var json = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        }
        return JsonDocument.Parse(json);
    }

    /// <summary>E.g. "192.168.1.50" → "https://192.168.1.50:8006". Proxmox answers on port 8006 by default.</summary>
    public static string Normalize(string url)
    {
        url = url.Trim();
        var scheme = "https";
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            scheme = url[..schemeEnd].ToLowerInvariant();
            url = url[(schemeEnd + 3)..];
        }
        var authority = url.Split('/')[0];
        return authority.Contains(':') ? $"{scheme}://{authority}" : $"{scheme}://{authority}:8006";
    }

    private static string Describe(Exception ex) => ex switch
    {
        InvalidOperationException => ex.Message,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => L.T("The token was refused", "Tokenet blev afvist"),
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => L.T("The token lacks permissions", "Tokenet mangler rettigheder"),
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } =>
            L.T("The certificate was refused – see Settings → Proxmox", "Certifikatet blev afvist – se Indstillinger → Proxmox"),
        HttpRequestException { StatusCode: null } or TaskCanceledException => L.T("No connection", "Ingen forbindelse"),
        _ => ex.Message
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static long Long(JsonElement e, string name) => (long)Num(e, name);
}
