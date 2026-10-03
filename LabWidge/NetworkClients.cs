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

    public static async Task<CloudflareRecord[]> GetRecordsAsync(string zoneId, string token, string type = "A", HttpClient? http = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records?type={type}&per_page=100");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await (http ?? Client).SendAsync(request).ConfigureAwait(true);
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

    public static async Task UpdateRecordAsync(string zoneId, string token, string recordId, CloudflareRecord recordInfo, string newIp, HttpClient? http = null)
    {
        var update = new CloudflareUpdateRequest
        {
            Type = recordInfo.Type,
            Name = recordInfo.Name,
            Content = newIp,
            Ttl = recordInfo.Ttl,
            Proxied = recordInfo.Proxied
        };

        var updateJson = JsonSerializer.Serialize(update);
        using var put = new HttpRequestMessage(HttpMethod.Put, $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records/{recordId}");
        put.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        put.Content = new StringContent(updateJson, System.Text.Encoding.UTF8, "application/json");

        using var putResp = await (http ?? Client).SendAsync(put).ConfigureAwait(true);
        if (!putResp.IsSuccessStatusCode)
        {
            var errorJson = await putResp.Content.ReadAsStringAsync();
            var errorMsg = ParseCloudflareError(errorJson) ?? $"Status: {putResp.StatusCode}";
            throw new HttpRequestException(L.T("Cloudflare error: ", "Cloudflare-fejl: ") + errorMsg);
        }
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

    public static async Task<int> UpdateAllARecordsAsync(string zoneId, string token, string ip, string[]? allowedHosts, HttpClient? http = null)
    {
        var allowed = NormalizeHosts(allowedHosts);
        if (allowed is { Length: 0 }) return 0;

        var records = await GetRecordsAsync(zoneId, token, "A", http);
        if (records.Length == 0) return 0;

        var updated = 0;

        foreach (var record in records)
        {
            if (allowed != null && !allowed.Contains(record.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(record.Content, ip, StringComparison.Ordinal))
            {
                continue;
            }

            await UpdateRecordAsync(zoneId, token, record.Id, record, ip, http);
            updated++;
        }

        return updated;
    }

    private static string[]? NormalizeHosts(string[]? hosts)
    {
        if (hosts == null)
        {
            return null;
        }

        return hosts
            .Select(h => h.Trim())
            .Where(h => h.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

        var client = new HttpClient(handler)
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

    public static async Task<string> FetchAsync(HttpClient client)
    {
        Exception? lastError = null;
        foreach (var endpoint in Endpoints)
        {
            try
            {
                var response = await client.GetStringAsync(endpoint).ConfigureAwait(true);
                var ip = response.Trim();

                if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    throw new InvalidOperationException("Invalid IP answer");
                }

                return ip;
            }
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

internal sealed class CloudflareUpdateRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "A";

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("ttl")]
    public int Ttl { get; set; }

    [JsonPropertyName("proxied")]
    public bool? Proxied { get; set; }
}
