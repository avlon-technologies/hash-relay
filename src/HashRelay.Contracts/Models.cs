namespace HashRelay.Contracts;

// ---------- HTTP API (/api/v1) ----------

public sealed record PublishRequest(
    IReadOnlyList<string> Tags,
    string Title,
    string? Body = null,
    string? Url = null,
    string? Priority = null,
    string? IdempotencyKey = null);

public sealed record PublishResult(
    Guid Id,
    IReadOnlyList<string> Tags,
    string Title,
    DateTimeOffset PublishedAt,
    int MatchedEndpoints,
    int DeliveredLive,
    int Queued,
    bool Duplicate);

public sealed record MeResponse(
    TenantInfo Tenant,
    ApiKeyInfo Key,
    DateTimeOffset ServerTime,
    string ServerVersion);

public sealed record TenantInfo(Guid Id, string Slug, string Name);

public sealed record ApiKeyInfo(Guid Id, string Label, IReadOnlyList<string> Scopes, string Hint);

public sealed record EndpointInfo(
    string EndpointId,
    string Name,
    string Platform,
    bool Online,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<string> Subscriptions);

public sealed record RegisterEndpointRequest(string Name, string Platform, string? AgentVersion = null);

public sealed record SubscriptionsRequest(IReadOnlyList<string> Tags);

public sealed record SubscriptionsResponse(string EndpointId, IReadOnlyList<string> Tags);

public sealed record NotificationSummary(
    Guid Id,
    IReadOnlyList<string> Tags,
    string Title,
    string? Body,
    string? Url,
    string Priority,
    DateTimeOffset PublishedAt,
    int MatchedEndpoints,
    int Delivered);

public sealed record ApiError(string Error, string Message, IReadOnlyDictionary<string, string[]>? Details = null);

public sealed record HealthResponse(string Status, string Version, DateTimeOffset Time);

// ---------- Realtime hub (/hubs/relay) ----------

public sealed record HelloRequest(string EndpointId, string Name, string Platform, string? AgentVersion);

public sealed record HelloResponse(
    TenantInfo Tenant,
    string EndpointId,
    string Name,
    IReadOnlyList<string> Subscriptions,
    DateTimeOffset ServerTime,
    int PendingReplay);

public sealed record NotificationMessage(
    Guid DeliveryId,
    Guid NotificationId,
    IReadOnlyList<string> Tags,
    string Title,
    string? Body,
    string? Url,
    string Priority,
    DateTimeOffset PublishedAt,
    string TenantName,
    bool Replayed);

public static class Priorities
{
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";
    public static readonly string[] All = [Low, Normal, High];
    public static string NormalizeOrDefault(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return Normal;
        var v = p.Trim().ToLowerInvariant();
        return All.Contains(v) ? v : throw new ArgumentException($"Priority must be one of: {string.Join(", ", All)}.");
    }
}

public static class Scopes
{
    public const string Publish = "publish";
    public const string Subscribe = "subscribe";
    public static readonly string[] All = [Publish, Subscribe];
}

public static class HubMethods
{
    // server -> client
    public const string Notify = "Notify";
    public const string SubscriptionsChanged = "SubscriptionsChanged";
    // client -> server
    public const string Hello = "Hello";
    public const string Subscribe = "Subscribe";
    public const string Unsubscribe = "Unsubscribe";
    public const string Ack = "Ack";
}
