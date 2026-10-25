using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Reads the Docker containers on every environment (host) Portainer manages, through the Portainer server's API.
/// Read-only: it never starts, stops or changes anything. Portainer usually has a self-signed certificate; it is accepted if
/// the user allowed it, and the fingerprint is remembered so a different certificate is refused later (trust on first use).
/// </summary>
internal sealed class PortainerService
{
    private readonly HttpClient _http;
    private readonly Func<string?> _readToken;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AppSettings _settings = new();

    public IReadOnlyList<DockerEnvironment> Environments { get; private set; } = Array.Empty<DockerEnvironment>();
    public string? LastError { get; private set; }
    public DateTime LastFetch { get; private set; } = DateTime.MinValue;

    public event Action? Updated;
    /// <summary>A new certificate fingerprint was remembered and must be saved in the settings.</summary>
    public event Action? CertificatePinned;

    /// <param name="handler">Only for tests: answers instead of the network (certificate checks are then the handler's business).</param>
    /// <param name="readToken">Only for tests: the access token instead of Credential Manager.</param>
    public PortainerService(HttpMessageHandler? handler = null, Func<string?>? readToken = null)
    {
        _readToken = readToken ?? CredentialStore.ReadPortainerToken;
        handler ??= new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            // A redirect is never Portainer's answer to an API call: it is a login page in front of it (see GetAsync)
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = ValidateCertificate }
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private bool ValidateCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (!_settings.PortainerAllowSelfSigned || cert == null) return false;

        var thumbprint = new X509Certificate2(cert).GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        if (string.IsNullOrEmpty(_settings.PortainerCertThumbprint))
        {
            _settings.PortainerCertThumbprint = thumbprint;
            Logger.Info($"Portainer certificate remembered: {thumbprint}");
            CertificatePinned?.Invoke();
            return true;
        }
        if (string.Equals(_settings.PortainerCertThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)) return true;

        Logger.Error($"The Portainer certificate changed ({thumbprint}) – the connection is refused.");
        return false;
    }

    public async Task RefreshAsync(AppSettings settings, CancellationToken cancel = default)
    {
        _settings = settings;
        if (!settings.HasDocker)
        {
            if (Environments.Count > 0 || LastError != null)
            {
                Environments = Array.Empty<DockerEnvironment>();
                LastError = null;
                if (!cancel.IsCancellationRequested) Updated?.Invoke();
            }
            return;
        }

        if (!await _lock.WaitAsync(0)) return;
        try
        {
            var environments = await FetchAsync(null, cancel);
            cancel.ThrowIfCancellationRequested();
            Environments = environments;
            LastError = null;
            LastFetch = DateTime.Now;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var error = Describe(ex);
            if (error != LastError) Logger.Error($"Portainer: {ex.Message}");
            LastError = error;
        }
        finally
        {
            _lock.Release();
            if (!cancel.IsCancellationRequested) Updated?.Invoke();
        }
    }

    /// <summary>Test from the settings: returns a description of what was found.</summary>
    public async Task<string> TestAsync(AppSettings settings, string token)
    {
        _settings = settings;
        var environments = await FetchAsync(token, CancellationToken.None);
        var up = environments.Count(e => e.Up);
        var containers = environments.Sum(e => e.Containers.Count);
        var hosts = environments.Count == 1 ? "1 host" : $"{environments.Count} hosts";
        return L.T($"{hosts} ({up} online), {containers} containers", $"{(environments.Count == 1 ? "1 vært" : $"{environments.Count} værter")} ({up} online), {containers} containere");
    }

    /// <summary>The environments, then the containers on each environment that is up, all at the same time.
    /// A host that does not answer is shown with its error instead of failing the whole update.</summary>
    private async Task<List<DockerEnvironment>> FetchAsync(string? token, CancellationToken cancel)
    {
        token ??= _readToken();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException(L.T("No access token saved", "Intet adgangstoken gemt"));
        var endpoints = PortainerJson.ParseEndpoints(await GetAsync("api/endpoints", token, cancel));
        var tasks = endpoints.Select(async e =>
        {
            if (!e.Up) return new DockerEnvironment(e.Id, e.Name, false, L.T("The agent does not answer", "Agenten svarer ikke"), Array.Empty<DockerContainer>());
            try
            {
                var json = await GetAsync($"api/endpoints/{e.Id}/docker/containers/json?all=1", token, cancel);
                return new DockerEnvironment(e.Id, e.Name, true, null, PortainerJson.ParseContainers(json));
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Logger.Error($"Portainer: environment {e.Name}: {ex.Message}");
                // Portainer answers 5xx when it cannot reach the host's agent
                var error = ex is HttpRequestException { StatusCode: { } code } && (int)code >= 500
                    ? L.T($"Portainer cannot reach the agent (HTTP {(int)code})", $"Portainer kan ikke nå agenten (HTTP {(int)code})")
                    : Describe(ex);
                return new DockerEnvironment(e.Id, e.Name, false, error, Array.Empty<DockerContainer>());
            }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    private async Task<string> GetAsync(string path, string token, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PortainerJson.Normalize(_settings.PortainerUrl ?? "")}/{path}");
        request.Headers.Add("X-API-Key", token.Trim());
        using var response = await _http.SendAsync(request, cancel).ConfigureAwait(true);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            // Cloudflare Access (or another login) sits in front of Portainer and wants a person to sign in
            var location = response.Headers.Location;
            throw new InvalidOperationException(location?.Host.EndsWith("cloudflareaccess.com", StringComparison.OrdinalIgnoreCase) == true
                ? L.T("Cloudflare Access is in front of Portainer – use its local address instead, e.g. https://localhost:9443 on the Portainer PC or the server's name on your network",
                      "Cloudflare Access står foran Portainer – brug i stedet den lokale adresse, fx https://localhost:9443 på Portainer-pc'en eller serverens navn på dit netværk")
                : L.T("The address sends LabWidge to a login page – use Portainer's own address", "Adressen sender LabWidge videre til en login-side – brug Portainers egen adresse"));
        }
        var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        return body;
    }

    /// <summary>Portainer's own page for an environment's containers, or its start page.</summary>
    public string WebUrl(DockerEnvironment? environment = null)
    {
        var baseUrl = PortainerJson.Normalize(_settings.PortainerUrl ?? "");
        return environment == null ? baseUrl : $"{baseUrl}/#!/{environment.Id}/docker/containers";
    }

    internal static string Describe(Exception ex) => ex switch
    {
        InvalidOperationException => ex.Message,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => L.T("The access token was refused", "Adgangstokenet blev afvist"),
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => L.T("The access token lacks permissions", "Adgangstokenet mangler rettigheder"),
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => L.T("Not found – is this the Portainer server's address?", "Ikke fundet – er det Portainer-serverens adresse?"),
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } =>
            L.T("The certificate was refused – see Settings → Docker", "Certifikatet blev afvist – se Indstillinger → Docker"),
        HttpRequestException { StatusCode: null } or TaskCanceledException => L.T("No connection", "Ingen forbindelse"),
        System.Text.Json.JsonException => L.T("Unexpected answer – is this the Portainer server's address?", "Uventet svar – er det Portainer-serverens adresse?"),
        _ => ex.Message
    };
}
