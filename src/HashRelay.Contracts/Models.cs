namespace HashRelay.Contracts;

// ---------- HTTP API (/api/v1) ----------

/// <summary>Publish a notification to one or more hashtags in the caller's tenant.</summary>
/// <param name="NotificationId">Optional client-supplied UUID. Re-sending the same id with an identical payload is idempotent; a different payload is rejected with 409.</param>
/// <param name="Markdown">Optional rich body (CommonMark + tables, task lists, ```mermaid fences). Rendered on the notification's web page; the toast shows <paramref name="Body"/> (derived from the first paragraph when omitted).</param>
/// <param name="ImageUrl">Optional absolute http(s) image URL shown inside the toast and on the page. Upload a file with <c>POST /api/v1/images</c> to get one.</param>
/// <param name="Diagram">Optional Mermaid diagram source. Rendered live on the page and to PNG for the toast. When omitted, the first ```mermaid fence in <paramref name="Markdown"/> is used.</param>
public sealed record PublishRequest(
    IReadOnlyList<string> Tags,
    string Title,
    string? Body = null,
    string? Url = null,
    string? Priority = null,
    Guid? NotificationId = null,
    string? Markdown = null,
    string? ImageUrl = null,
    string? Diagram = null);

public sealed record PublishResult(
    Guid NotificationId,
    DateTimeOffset AcceptedAtUtc,
    IReadOnlyList<string> Tags,
    string Title,
    int MatchedEndpoints,
    int DeliveredLive,
    int Queued,
    bool Duplicate,
    string? DetailUrl = null,
    string? ImageUrl = null,
    int Pushed = 0);

/// <summary>Result of <c>POST /api/v1/images</c>: a public, unguessable image URL usable as <see cref="PublishRequest.ImageUrl"/>.</summary>
public sealed record ImageUploadResult(string Id, string Url, long Bytes, string ContentType);

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
    IReadOnlyList<string> Subscriptions,
    string? PushProvider = null);

/// <param name="PushProvider">Push provider of <paramref name="PushToken"/> ("fcm"). When given with an empty token, the endpoint's push token is cleared; when null, it is left unchanged.</param>
/// <param name="PushToken">The provider registration token of a mobile endpoint. Re-send whenever the platform rotates it.</param>
public sealed record RegisterEndpointRequest(string? EndpointId, string Platform, string? Name = null, string? AgentVersion = null, string? PushProvider = null, string? PushToken = null);

public sealed record SubscriptionsRequest(IReadOnlyList<string> Tags);

public sealed record SubscriptionsResponse(string EndpointId, IReadOnlyList<string> Tags);

/// <summary>
/// Who published a notification, as established by the server from the authenticated credential, never from the
/// request body. <paramref name="Id"/> is stable for the principal (service account, API key or paired device),
/// <paramref name="Type"/> is a <see cref="PublisherTypes"/> value and <paramref name="Channel"/> a <see cref="Channels"/>
/// value. Notifications stored before attribution existed carry <see cref="Unknown"/>.
/// </summary>
public sealed record PublisherInfo(Guid? Id, string? Name, string Type, string? Channel)
{
    public static readonly PublisherInfo Unknown = new(null, null, PublisherTypes.Unknown, null);
    public bool IsKnown => Name is not null;
}

/// <summary>One delivery routed to an endpoint (its inbox). <paramref name="Status"/> is a <c>DeliveryStatus</c> value: queued, sent, pushed, acked or expired.</summary>
public sealed record EndpointDeliveryInfo(
    Guid DeliveryId,
    string Status,
    Guid NotificationId,
    IReadOnlyList<string> Tags,
    string Title,
    string? Body,
    string? Url,
    string Priority,
    DateTimeOffset PublishedAtUtc,
    string? ImageUrl,
    string? DetailUrl,
    bool Rich,
    DateTimeOffset? AckedAtUtc,
    PublisherInfo? Publisher = null);

public sealed record NotificationSummary(
    Guid NotificationId,
    IReadOnlyList<string> Tags,
    string Title,
    string? Body,
    string? Url,
    string Priority,
    DateTimeOffset PublishedAtUtc,
    int MatchedEndpoints,
    int Delivered,
    bool HasMarkdown = false,
    bool HasDiagram = false,
    string? ImageUrl = null,
    string? DetailUrl = null,
    PublisherInfo? Publisher = null);

/// <summary>Error envelope: <c>{code,message,traceId}</c> plus optional field-level details.</summary>
public sealed record ApiError(string Code, string Message, string? TraceId = null, IReadOnlyDictionary<string, string[]>? Details = null);

public sealed record HealthResponse(string Status, string Version, DateTimeOffset Time);

// ---------- Device pairing (OAuth device-authorization style) ----------

/// <param name="Client">One of <see cref="PairingClients"/>: "agent", "cli" or "mobile".</param>
public sealed record DeviceCodeRequest(string EndpointId, string DeviceName, string Platform, string? AgentVersion, string Client);

public sealed record DeviceCodeResponse(string DeviceCode, string UserCode, string VerificationUrl, string VerificationUrlComplete, int ExpiresInSeconds, int IntervalSeconds);

public sealed record DeviceTokenRequest(string DeviceCode);

/// <param name="Status">"pending", "approved", "denied" or "expired".</param>
public sealed record DeviceTokenResponse(string Status, string? Token, TenantInfo? Tenant, CredentialInfo? Credential);

// ---------- Realtime hub (/hubs/notifications) ----------

/// <param name="PushProvider">Optional push provider ("fcm") of <paramref name="PushToken"/>; mobile apps send their current token on every connect so rotations are picked up.</param>
public sealed record HelloRequest(string EndpointId, string Name, string Platform, string? AgentVersion, string? PushProvider = null, string? PushToken = null);

public sealed record HelloResponse(
    TenantInfo Tenant,
    string EndpointId,
    string Name,
    IReadOnlyList<string> Subscriptions,
    DateTimeOffset ServerTime,
    int PendingReplay);

/// <summary>Server → client callback payload for <see cref="HubMethods.NotificationReceived"/>.</summary>
/// <param name="ImageUrl">Image to show inside the toast: the publisher's image, or the server-rendered diagram.</param>
/// <param name="DetailUrl">Web page with the full notification (rendered markdown, live diagram). Opened on click when <paramref name="Rich"/>.</param>
/// <param name="Rich">True when the notification carries markdown or a diagram beyond what the toast can show.</param>
/// <param name="Replayed">True when this is backlog from while the endpoint was offline (sent during Hello), not a live publication. Clients fold replayed deliveries into a catch-up summary instead of a toast each.</param>
/// <param name="Publisher">Server-verified publisher attribution; null for notifications stored before attribution existed.</param>
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
    bool Replayed,
    string? ImageUrl = null,
    string? DetailUrl = null,
    bool Rich = false,
    PublisherInfo? Publisher = null);

public static class Limits
{
    public const int TitleMax = 120;
    public const int BodyMax = 1000;
    public const int UrlMax = 2048;
    public const int LabelMax = 80;
    public const int EndpointIdMax = 64;
    public const int DeviceNameMax = 80;
    public const int MarkdownMax = 20_000;
    public const int DiagramMax = 10_000;
    public const int ImageMaxBytes = 3 * 1024 * 1024;
    public static readonly string[] ImageContentTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];
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

/// <summary>
/// What kind of principal a publisher is. Declared by a workspace admin when the credential is registered (API key,
/// service account, OAuth consent), never inferred from the transport or the message text; a paired device is
/// <see cref="Human"/> because pairing is approved interactively for a person's own device. Anything undeclared is <see cref="Unknown"/>.
/// </summary>
public static class PublisherTypes
{
    public const string Agent = "agent";
    public const string Application = "application";
    public const string Service = "service";
    public const string Human = "human";
    public const string Unknown = "unknown";
    public static readonly string[] All = [Agent, Application, Service, Human, Unknown];
    /// <summary>The types an admin may declare for a credential (a device is always <see cref="Human"/>).</summary>
    public static readonly string[] Declarable = [Unknown, Agent, Application, Service];
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [Agent] = "AI agent",
        [Application] = "Application",
        [Service] = "Service / automation",
        [Human] = "Person",
        [Unknown] = "Not specified",
    };
    public static string Normalize(string? type)
    {
        var v = (type ?? "").Trim().ToLowerInvariant();
        return All.Contains(v) ? v : Unknown;
    }
}

/// <summary>The entry point a publication arrived through, determined by the server from the request (path, then the first-party client's User-Agent). A hint for display and audit, not an identity.</summary>
public static class Channels
{
    public const string Mcp = "mcp";
    public const string Rest = "rest";
    public const string Sdk = "sdk";
    public const string Cli = "cli";
    public const string Agent = "agent";
    public const string Mobile = "mobile";
    public static readonly string[] All = [Mcp, Rest, Sdk, Cli, Agent, Mobile];
}

public static class Scopes
{
    public const string Publish = "publish";
    public const string Subscribe = "subscribe";
    public static readonly string[] All = [Publish, Subscribe];
}

/// <summary>Installed clients that can start the device-pairing flow.</summary>
public static class PairingClients
{
    public const string Agent = "agent";
    public const string Cli = "cli";
    public const string Mobile = "mobile";
    public static readonly string[] All = [Agent, Cli, Mobile];
}

/// <summary>Push providers a mobile endpoint can register a token for. Firebase Cloud Messaging serves both Android and iOS (relayed to APNs by Firebase).</summary>
public static class PushProviders
{
    public const string Fcm = "fcm";
    public static readonly string[] All = [Fcm];
    public const int TokenMax = 4096;
}

public static class CredentialKinds
{
    public const string Api = "api";
    public const string Device = "device";
    /// <summary>Key of a service account: an agent or automation principal with fine-grained scopes and a tag allowlist.</summary>
    public const string Service = "service";
}

/// <summary>Fine-grained scopes for service accounts (MCP and REST). Each implies a legacy API scope so existing policies apply.</summary>
public static class ServiceScopes
{
    public const string Publish = "notifications:publish";
    public const string Read = "notifications:read";
    public const string Endpoints = "endpoints:read";
    public const string Subscriptions = "subscriptions:write";
    public const string Images = "images:write";
    public static readonly string[] All = [Publish, Read, Endpoints, Subscriptions, Images];
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [Publish] = "Publish notifications to the allowed hashtags",
        [Read] = "Read recent notifications in the workspace",
        [Endpoints] = "List devices and their subscriptions",
        [Subscriptions] = "Change device subscriptions",
        [Images] = "Upload images for notifications",
    };

    /// <summary>The legacy scope claims ("publish", "subscribe") implied by a set of service scopes.</summary>
    public static IEnumerable<string> Implied(IEnumerable<string> scopes)
    {
        var set = scopes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (set.Contains(Publish) || set.Contains(Images)) yield return Scopes.Publish;
        if (set.Contains(Subscriptions)) yield return Scopes.Subscribe;
    }
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
