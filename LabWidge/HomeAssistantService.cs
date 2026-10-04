using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>An entity in Home Assistant, e.g. a light, a switch or a sensor.</summary>
internal sealed record HaEntity(string EntityId, string Name, string State, int? Brightness, string? Unit)
{
    private static CultureInfo Da => L.Culture;

    public string Domain => EntityId.Split('.')[0];

    /// <summary>Domains where Home Assistant has a toggle service, so the widget can switch them on and off.</summary>
    public bool CanToggle => Domain is "light" or "switch" or "fan" or "input_boolean" or "siren";

    public bool IsOn => State.Equals("on", StringComparison.OrdinalIgnoreCase);

    public bool IsUnavailable =>
        State.Equals("unavailable", StringComparison.OrdinalIgnoreCase)
        || State.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>Brightness in percent (Home Assistant reports 0-255).</summary>
    public int? BrightnessPercent => Brightness is int b && b > 0 ? Math.Max(1, (int)Math.Round(b * 100.0 / 255)) : null;

    /// <summary>The text on the right in the widget.</summary>
    public string Display()
    {
        if (IsUnavailable) return L.T("unavailable", "utilgængelig");
        if (CanToggle) return IsOn ? L.T("On", "Tændt") : L.T("Off", "Slukket");

        var value = double.TryParse(State, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(Math.Abs(number) >= 100 || number == Math.Floor(number) ? "0.#" : "0.0", Da)
            : Translate(State);

        return Unit is { Length: > 0 } unit ? $"{value} {unit}" : value;
    }

    private static string Translate(string state) => state.ToLowerInvariant() switch
    {
        "on" => L.T("On", "Til"),
        "off" => L.T("Off", "Fra"),
        "home" => L.T("Home", "Hjemme"),
        "not_home" => L.T("Away", "Ude"),
        "open" => L.T("Open", "Åben"),
        "closed" => L.T("Closed", "Lukket"),
        "locked" => L.T("Locked", "Låst"),
        "unlocked" => L.T("Unlocked", "Ulåst"),
        "idle" => L.T("Idle", "Inaktiv"),
        "playing" => L.T("Playing", "Afspiller"),
        "paused" => L.T("Paused", "På pause"),
        "heat" => L.T("Heat", "Varme"),
        "cool" => L.T("Cool", "Køl"),
        _ => state
    };
}

/// <summary>
/// Reads and controls selected entities through Home Assistant's REST API. Only the entities the user
/// chose are fetched, so a large installation costs no more than a few small calls.
/// </summary>
internal sealed class HomeAssistantService
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<HaEntity> Entities { get; private set; } = Array.Empty<HaEntity>();
    public string? LastError { get; private set; }
    public DateTime LastFetch { get; private set; } = DateTime.MinValue;

    public event Action? Updated;

    public HomeAssistantService(HttpClient http) => _http = http;
    public CancellationToken Lifetime { get; set; }

    /// <summary>True while an on/off call is under way, so the widget can show the row as pending.</summary>
    public bool IsPending(string entityId) => _pending.Contains(entityId);

    public async Task RefreshAsync(AppSettings settings, CancellationToken cancel = default)
    {
        if (!settings.HasHomeAssistant)
        {
            if (Entities.Count > 0 || LastError != null)
            {
                Entities = Array.Empty<HaEntity>();
                LastError = null;
                if (!cancel.IsCancellationRequested) Updated?.Invoke();
            }
            return;
        }

        if (!await _lock.WaitAsync(0)) return;
        try
        {
            var token = CredentialStore.ReadHomeAssistantToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                LastError = L.T("No token saved", "Intet token gemt");
                Entities = Array.Empty<HaEntity>();
                return;
            }

            var baseUrl = Normalize(settings.HomeAssistantUrl!);
            var ids = settings.HomeAssistantEntities;
            var results = await Task.WhenAll(ids.Select(id => FetchOneAsync(baseUrl, token!, id, cancel)));

            // The order follows the user's choice, and entities that failed are shown as unavailable
            var list = new List<HaEntity>(ids.Length);
            string? error = null;
            for (var i = 0; i < ids.Length; i++)
            {
                list.Add(results[i].Entity ?? new HaEntity(ids[i], FallbackName(ids[i]), "unavailable", null, null));
                error ??= results[i].Error;
            }

            cancel.ThrowIfCancellationRequested();
            Entities = list;
            LastError = list.All(e => e.IsUnavailable) ? error : null;
            if (LastError == null) LastFetch = DateTime.Now;
            if (LastError != null) Logger.Error($"Home Assistant: {LastError}");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Logger.Error($"Home Assistant failed: {ex.Message}");
        }
        finally
        {
            _lock.Release();
            if (!cancel.IsCancellationRequested) Updated?.Invoke();
        }
    }

    /// <summary>Switches an entity on or off and updates the row with Home Assistant's answer.</summary>
    public async Task ToggleAsync(string entityId, AppSettings settings)
    {
        var cancel = Lifetime;
        if (cancel.IsCancellationRequested) return;
        if (!settings.HasHomeAssistant || _pending.Contains(entityId)) return;

        var token = CredentialStore.ReadHomeAssistantToken();
        if (string.IsNullOrWhiteSpace(token)) return;

        var domain = entityId.Split('.')[0];
        _pending.Add(entityId);
        Updated?.Invoke();
        try
        {
            var baseUrl = Normalize(settings.HomeAssistantUrl!);
            using var request = Authorized(HttpMethod.Post, $"{baseUrl}/api/services/{domain}/toggle", token!);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new Dictionary<string, string> { ["entity_id"] = entityId }),
                Encoding.UTF8, "application/json");

            using (var response = await _http.SendAsync(request, cancel))
            {
                response.EnsureSuccessStatusCode();
            }

            // The answer to a service call does not always contain the new state – e.g. not for lights
            // with a transition time. The state is therefore fetched directly, so the row changes at once
            // instead of staying unchanged until the next refresh up to 15 seconds later.
            await ReadBackAsync(baseUrl, token!, entityId, cancel);
            Logger.Info($"Home Assistant: {entityId} toggled.");

            _pending.Remove(entityId);
            if (!cancel.IsCancellationRequested) Updated?.Invoke();

            // A transition is rarely finished at once; an extra look catches the final
            // brightness without the row continuing to look busy.
            await Task.Delay(900, cancel);
            if (await ReadBackAsync(baseUrl, token!, entityId, cancel)) Updated?.Invoke();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Logger.Error($"Home Assistant could not toggle {entityId}: {ex.Message}");
        }
        finally
        {
            _pending.Remove(entityId);
            if (!cancel.IsCancellationRequested) Updated?.Invoke();
        }
    }

    /// <summary>Fetches one entity's state and puts it in the list. True if something changed.</summary>
    private async Task<bool> ReadBackAsync(string baseUrl, string token, string entityId, CancellationToken cancel)
    {
        var (entity, _) = await FetchOneAsync(baseUrl, token, entityId, cancel);
        if (entity == null) return false;

        var current = Entities.FirstOrDefault(e => e.EntityId == entity.EntityId);
        if (current == entity) return false;

        Entities = Entities.Select(e => e.EntityId == entity.EntityId ? entity : e).ToList();
        return true;
    }

    private async Task<(HaEntity? Entity, string? Error)> FetchOneAsync(string baseUrl, string token, string entityId, CancellationToken cancel = default)
    {
        try
        {
            using var request = Authorized(HttpMethod.Get, $"{baseUrl}/api/states/{Uri.EscapeDataString(entityId)}", token);
            using var response = await _http.SendAsync(request, cancel);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, entityId + L.T(" does not exist", " findes ikke"));
            }
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            return (Parse(doc.RootElement), null);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (null, Describe(ex));
        }
    }

    // ---------- Shared ----------

    /// <summary>Fetches all entities – used when the user chooses what the widget shows.</summary>
    public static async Task<IReadOnlyList<HaEntity>> FetchAllAsync(HttpClient http, string baseUrl, string token, CancellationToken cancel = default)
    {
        using var request = Authorized(HttpMethod.Get, $"{Normalize(baseUrl)}/api/states", token);
        using var response = await http.SendAsync(request, cancel);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(L.T("Home Assistant refused the token.", "Tokenet blev afvist af Home Assistant."));
        }
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        var list = new List<HaEntity>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (Parse(element) is HaEntity entity) list.Add(entity);
        }
        return list
            .OrderBy(e => e.Domain, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Create(CultureInfo.GetCultureInfo("da-DK"), ignoreCase: true))
            .ToList();
    }

    private static HaEntity? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("entity_id", out var idElement) || idElement.ValueKind != JsonValueKind.String) return null;

        var id = idElement.GetString()!;
        var state = element.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "unknown";

        string? name = null, unit = null;
        int? brightness = null;
        if (element.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
        {
            if (attributes.TryGetProperty("friendly_name", out var f) && f.ValueKind == JsonValueKind.String) name = f.GetString();
            if (attributes.TryGetProperty("unit_of_measurement", out var u) && u.ValueKind == JsonValueKind.String) unit = u.GetString();
            if (attributes.TryGetProperty("brightness", out var b) && b.ValueKind == JsonValueKind.Number) brightness = b.GetInt32();
        }

        return new HaEntity(id, string.IsNullOrWhiteSpace(name) ? FallbackName(id) : name!, state, brightness, unit);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>Without a trailing slash, and with http:// if the user only typed a host name.</summary>
    public static string Normalize(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }
        return trimmed;
    }

    private static string FallbackName(string entityId)
    {
        var name = entityId.Contains('.') ? entityId[(entityId.IndexOf('.') + 1)..] : entityId;
        return name.Replace('_', ' ');
    }

    /// <summary>Turns network errors into something a user can act on.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => L.T("The token was refused", "Tokenet blev afvist"),
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => L.T("The address responds but has no Home Assistant API", "Adressen svarer, men har ikke et Home Assistant-API"),
        TaskCanceledException or OperationCanceledException => L.T("Home Assistant did not answer in time", "Home Assistant svarede ikke i tide"),
        HttpRequestException => L.T("Could not connect to the address", "Kunne ikke få forbindelse til adressen"),
        _ => ex.Message
    };

    /// <summary>True when the error looks like an address that cannot be reached from here – typically NAT hairpin.</summary>
    public static bool LooksUnreachable(Exception ex) =>
        ex is TaskCanceledException or OperationCanceledException or HttpRequestException { StatusCode: null };
}
