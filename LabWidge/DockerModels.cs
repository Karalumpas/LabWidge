using System.Text.Json;

internal enum ContainerHealth { None, Healthy, Starting, Unhealthy }

/// <summary>A Docker container as Portainer reports it.</summary>
internal sealed record DockerContainer(string Id, string Name, string Image, string State, string Status, DateTime Created,
    ContainerHealth Health, string? Stack)
{
    public bool IsRunning => State == "running";
    /// <summary>Stopped with an error, restarting, or reported unhealthy by its health check.</summary>
    public bool NeedsAttention => Health == ContainerHealth.Unhealthy || State is "restarting" or "dead"
        || (State == "exited" && ExitCode is int code && code != 0);
    /// <summary>The exit code in "Exited (137) 3 hours ago", if any.</summary>
    public int? ExitCode
    {
        get
        {
            var open = Status.IndexOf('(');
            var close = open < 0 ? -1 : Status.IndexOf(')', open);
            return Status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase) && close > open
                   && int.TryParse(Status[(open + 1)..close], out var code) ? code : null;
        }
    }
}

/// <summary>A Portainer environment – one Docker host, e.g. a server with the Portainer agent.</summary>
internal sealed record DockerEnvironment(int Id, string Name, bool Up, string? Error, IReadOnlyList<DockerContainer> Containers)
{
    public int Running => Containers.Count(c => c.IsRunning);
    public int Stopped => Containers.Count - Running;
    public int Attention => Containers.Count(c => c.NeedsAttention);
}

/// <summary>Pure parsing of Portainer's API answers, so it can be tested without a server.</summary>
internal static class PortainerJson
{
    /// <summary>Portainer endpoint types that run Docker: local socket, agent and Edge agent. Kubernetes is left out.</summary>
    private static readonly int[] DockerTypes = { 1, 2, 4 };

    /// <summary>GET /api/endpoints → (id, name, up) for each Docker environment, in Portainer's order.</summary>
    public static List<(int Id, string Name, bool Up)> ParseEndpoints(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<(int, string, bool)>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var type = Int(e, "Type") ?? 1;
            if (!DockerTypes.Contains(type)) continue;
            if (Int(e, "Id") is not int id) continue;
            result.Add((id, Str(e, "Name") ?? $"#{id}", (Int(e, "Status") ?? 1) == 1));
        }
        return result;
    }

    /// <summary>GET /api/endpoints/{id}/docker/containers/json?all=1 → containers, running first, then by name.</summary>
    public static List<DockerContainer> ParseContainers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<DockerContainer>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var name = c.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.Array && names.GetArrayLength() > 0
                ? (names[0].GetString() ?? "").TrimStart('/') : "";
            var id = Str(c, "Id") ?? "";
            if (name.Length == 0) name = id.Length >= 12 ? id[..12] : id;
            var status = Str(c, "Status") ?? "";
            string? stack = null;
            if (c.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
                stack = Str(labels, "com.docker.compose.project") ?? Str(labels, "com.docker.stack.namespace");
            var created = c.TryGetProperty("Created", out var cr) && cr.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(cr.GetInt64()).LocalDateTime : DateTime.MinValue;
            result.Add(new DockerContainer(id, name, Str(c, "Image") ?? "", (Str(c, "State") ?? "unknown").ToLowerInvariant(),
                status, created, Health(status), string.IsNullOrWhiteSpace(stack) ? null : stack));
        }
        return result.OrderByDescending(c => c.IsRunning).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Docker puts the health in the status text: "Up 3 hours (healthy)", "(unhealthy)", "(health: starting)".</summary>
    public static ContainerHealth Health(string status) =>
        status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase) ? ContainerHealth.Unhealthy
        : status.Contains("(health: starting)", StringComparison.OrdinalIgnoreCase) ? ContainerHealth.Starting
        : status.Contains("(healthy)", StringComparison.OrdinalIgnoreCase) ? ContainerHealth.Healthy
        : ContainerHealth.None;

    /// <summary>E.g. "mgserver" → "https://mgserver:9443". Portainer answers on HTTPS port 9443 by default.</summary>
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
        if (authority.Length == 0) return "";
        // An IPv6 address has colons of its own; only a colon after "]" is a port
        var hasPort = authority.StartsWith('[') ? authority.Contains("]:") : authority.Contains(':');
        return hasPort ? $"{scheme}://{authority}" : $"{scheme}://{authority}:{(scheme == "http" ? 9000 : 9443)}";
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
