using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class CloudflareClient
{
    private static readonly HttpClient Client = HttpClientFactory.Create(TimeSpan.FromSeconds(30));

    public static async Task<string> FetchExternalIpAsync()
    {
        return await ExternalIpProvider.FetchAsync(Client).ConfigureAwait(true);
    }

    public static async Task<CloudflareRecord[]> GetRecordsAsync(string zoneId, string token, string type = "A", HttpClient? http = null, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records?type={type}&per_page=100");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await (http ?? Client).SendAsync(request, cancel).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        var list = JsonSerializer.Deserialize<CloudflareListResponse>(json);

        return list?.Result ?? Array.Empty<CloudflareRecord>();
    }

    public static async Task<int> CountTunnelsAsync(string accountId, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.cloudflare.com/client/v4/accounts/{accountId}/cfd_tunnel?is_deleted=false&per_page=50");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await Client.SendAsync(request).ConfigureAwait(true);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(ParseCloudflareError(json) ?? $"Status: {response.StatusCode}");
        }
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("result").GetArrayLength();
    }

    private static string? ParseCloudflareError(string json)
    {
        try
        {
            // Simple manual parse or search for "message" to avoid defining full error structures
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            {
                var firstError = errors[0];
                if (firstError.TryGetProperty("message", out var msg))
                {
                    return msg.GetString();
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Cloudflare is a monitoring integration. Refuse writes before any network request is sent.</summary>
internal sealed class CloudflareReadOnlyHandler : DelegatingHandler
{
    public CloudflareReadOnlyHandler(HttpMessageHandler inner) : base(inner) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        if (string.Equals(request.RequestUri?.Host, "api.cloudflare.com", StringComparison.OrdinalIgnoreCase)
            && request.Method != HttpMethod.Get)
            throw new InvalidOperationException("Cloudflare monitoring is read-only.");
        return base.SendAsync(request, cancel);
    }
}

internal static class HttpClientFactory
{
    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = WebRequest.GetSystemWebProxy(),
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            Credentials = CredentialCache.DefaultCredentials
        };
        if (handler.Proxy != null)
        {
            handler.Proxy.Credentials = CredentialCache.DefaultCredentials;
        }

        var client = new HttpClient(new CloudflareReadOnlyHandler(handler))
        {
            Timeout = timeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LabWidge/1.0");
        return client;
    }
}

internal static class ExternalIpProvider
{
    private static readonly string[] Endpoints =
    {
        "https://ipv4.icanhazip.com",
        "https://api.ipify.org",
        "https://ifconfig.me/ip",
        "http://api.ipify.org"
    };

    public static async Task<string> FetchAsync(HttpClient client, CancellationToken cancel = default)
    {
        Exception? lastError = null;
        foreach (var endpoint in Endpoints)
        {
            try
            {
                var response = await client.GetStringAsync(endpoint, cancel).ConfigureAwait(true);
                var ip = response.Trim();

                if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    throw new InvalidOperationException("Invalid IP answer");
                }

                return ip;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                Logger.Error($"IP fetch failed for {endpoint}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            L.T("Could not get the external IP from any service.", "Kunne ikke hente ekstern IP fra nogen tjeneste."),
            lastError);
    }
}

internal sealed class CloudflareListResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("result")]
    public CloudflareRecord[]? Result { get; set; }
}

internal sealed class CloudflareRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("ttl")]
    public int Ttl { get; set; }

    [JsonPropertyName("proxied")]
    public bool? Proxied { get; set; }
}
