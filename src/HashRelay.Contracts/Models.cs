namespace HashRelay.Contracts;

// ---------- HTTP API (/api/v1) ----------

/// <summary>Publish a notification to one or more hashtags in the caller's tenant.</summary>
/// <param name="NotificationId">Optional client-supplied UUID. Re-sending the same id with an identical payload is idempotent; a different payload is rejected with 409.</param>
public sealed record PublishRequest(
    IReadOnlyList<string> Tags,
    string Title,
    string? Body = null,
    string? Url = null,
    string? Priority = null,
    Guid? NotificationId = null);

public sealed record PublishResult(
    Guid NotificationId,
    DateTimeOffset AcceptedAtUtc,
    IReadOnlyList<string> Tags,
    string Title,
    int MatchedEndpoints,
    int DeliveredLive,
    int Queued,
    bool Duplicate);

public sealed record MeResponse(
    TenantInfo Tenant,
    CredentialInfo Credential,
    DateTimeOffset ServerTime,
    string ServerVersion);

public sealed record TenantInfo(Guid Id, string Slug, string Name);

/// <param name="Kind">"api" for a tenant publisher key, "device" for a paired subscriber device token.</param>
public sealed record CredentialInfo(Guid Id, string Label, string Kind, IReadOnlyList<string> Scopes, string Hint);

public sealed record EndpointInfo(
    string EndpointId,
    string Name,
    string Platform,
    bool Online,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<string> Subscriptions);

public sealed record RegisterEndpointRequest(string? EndpointId, string Platform, string? Name = null, string? AgentVersion = null);

public sealed record SubscriptionsRequest(IReadOnlyList<string> Tags);

public sealed record SubscriptionsResponse(string EndpointId, IReadOnlyList<string> Tags);

public sealed record NotificationSummary(
    Guid NotificationId,
    IReadOnlyList<string> Tags,
    string Title,
    string? Body,
    string? Url,
    string Priority,
    DateTimeOffset PublishedAtUtc,
    int MatchedEndpoints,
    int Delivered);

/// <summary>Error envelope: <c>{code,message,traceId}</c> plus optional field-level details.</summary>
public sealed record ApiError(string Code, string Message, string? TraceId = null, IReadOnlyDictionary<string, string[]>? Details = null);

public sealed record HealthResponse(string Status, string Version, DateTimeOffset Time);

// ---------- Device pairing (OAuth device-authorization style) ----------

public sealed record DeviceCodeRequest(string EndpointId, string DeviceName, string Platform, string? AgentVersion, string Client);

public sealed record DeviceCodeResponse(string DeviceCode, string UserCode, string VerificationUrl, string VerificationUrlComplete, int ExpiresInSeconds, int IntervalSeconds);

public sealed record DeviceTokenRequest(string DeviceCode);

/// <param name="Status">"pending", "approved", "denied" or "expired".</param>
public sealed record DeviceTokenResponse(string Status, string? Token, TenantInfo? Tenant, CredentialInfo? Credential);

// ---------- Realtime hub (/hubs/notifications) ----------

public sealed record HelloRequest(string EndpointId, string Name, string Platform, string? AgentVersion);

public sealed record HelloResponse(
    TenantInfo Tenant,
    string EndpointId,
    string Name,
    IReadOnlyList<string> Subscriptions,
    DateTimeOffset ServerTime,
    int PendingReplay);

/// <summary>Server → client callback payload for <see cref="HubMethods.NotificationReceived"/>.</summary>
public sealed record NotificationMessage(
    Guid NotificationId,
    Guid TenantId,
    IReadOnlyList<string> Tags,
    string Title,
    string? Body,
    DateTimeOffset PublishedAtUtc,
    Guid DeliveryId,
    string? Url,
    string Priority,
    string TenantName,
    bool Replayed);

public static class Limits
{
    public const int TitleMax = 120;
    public const int BodyMax = 1000;
    public const int UrlMax = 2048;
    public const int LabelMax = 80;
    public const int EndpointIdMax = 64;
    public const int DeviceNameMax = 80;
}

public static class Priorities
{
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";
    public static readonly string[] All = [Low, Normal, High];
    public static bool TryNormalize(string? p, out string value)
    {
        value = Normal;
        if (string.IsNullOrWhiteSpace(p)) return true;
        var v = p.Trim().ToLowerInvariant();
        if (!All.Contains(v)) return false;
        value = v;
        return true;
    }
}

public static class Scopes
{
    public const string Publish = "publish";
    public const string Subscribe = "subscribe";
    public static readonly string[] All = [Publish, Subscribe];
}

public static class CredentialKinds
{
    public const string Api = "api";
    public const string Device = "device";
}

public static class HubMethods
{
    public const string Path = "/hubs/notifications";
    // server -> client
    public const string NotificationReceived = "NotificationReceived";
    public const string SubscriptionsChanged = "SubscriptionsChanged";
    public const string CredentialRevoked = "CredentialRevoked";
    // client -> server
    public const string Hello = "Hello";
    public const string Subscribe = "Subscribe";
    public const string Unsubscribe = "Unsubscribe";
    public const string Ack = "Ack";
}

public static class ErrorCodes
{
    public const string Validation = "validation_failed";
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string Throttled = "throttled";
    public const string Unexpected = "unexpected";
}
