namespace HashRelay.Server.Data;

public sealed class Account
{
    public Guid Id { get; set; }
    public required string Email { get; set; }          // normalised lowercase, unique
    public required string DisplayName { get; set; }
    public required string PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public List<Membership> Memberships { get; set; } = [];
}

public sealed class Tenant
{
    public Guid Id { get; set; }
    public required string Slug { get; set; }           // unique, url-safe
    public required string Name { get; set; }
    public Guid OwnerAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<Membership> Memberships { get; set; } = [];
}

public static class Roles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";
    public static bool CanManageKeys(string role) => role is Owner or Admin;
}

public sealed class Membership
{
    public Guid TenantId { get; set; }
    public Guid AccountId { get; set; }
    public required string Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Tenant Tenant { get; set; } = null!;
    public Account Account { get; set; } = null!;
}

/// <summary>
/// A tenant-bound credential. Only a SHA-256 verifier of the secret is stored; the plaintext is shown once.
/// Kind "api" = trusted publisher integration key issued in the dashboard; kind "device" = subscriber token
/// issued to one paired endpoint through the device-pairing flow.
/// </summary>
public sealed class Credential
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Kind { get; set; }
    public required string Label { get; set; }
    public required string Hash { get; set; }           // hex SHA-256 of the full secret, unique
    public required string Hint { get; set; }           // prefix + last 4 for display, e.g. "hr_live_…9f3a"
    public required string Scopes { get; set; }         // comma separated
    public Guid? EndpointRef { get; set; }              // device tokens are bound to one endpoint
    public Guid? ServiceAccountId { get; set; }         // service keys belong to one service account
    public Guid? CreatedByAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
    public IReadOnlyList<string> ScopeList => Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool HasScope(string scope) => ScopeList.Contains(scope, StringComparer.OrdinalIgnoreCase);
}

public sealed class Endpoint
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string EndpointId { get; set; }     // stable client-provided id, unique within tenant
    public required string Name { get; set; }
    public required string Platform { get; set; }
    public string? AgentVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastDisconnectedAt { get; set; }
    /// <summary>Push provider ("fcm") of <see cref="PushToken"/>; null for endpoints reached only over the realtime hub.</summary>
    public string? PushProvider { get; set; }
    /// <summary>Provider registration token of a mobile endpoint. Treated as a secret: never shown in the dashboard or API.</summary>
    public string? PushToken { get; set; }
    public DateTimeOffset? PushTokenUpdatedAt { get; set; }
    /// <summary>Set when the provider reported the last token as unregistered; cleared when a new token is registered.</summary>
    public DateTimeOffset? PushInvalidatedAt { get; set; }
    public List<Subscription> Subscriptions { get; set; } = [];

    public bool CanPush => PushToken is not null && PushProvider is not null;
}

public sealed class Subscription
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EndpointRef { get; set; }
    public required string Tag { get; set; }            // normalised, no '#'
    public DateTimeOffset CreatedAt { get; set; }
    public Endpoint Endpoint { get; set; } = null!;
}

public sealed class Notification
{
    public Guid Id { get; set; }                        // == public notificationId
    public Guid TenantId { get; set; }
    public required string TagsCsv { get; set; }
    public required string Title { get; set; }
    public string? Body { get; set; }
    public string? Url { get; set; }
    public required string Priority { get; set; }
    public string? Markdown { get; set; }               // rich body, rendered on /n/{id}
    public string? ImageUrl { get; set; }               // publisher-supplied image (absolute URL)
    public string? Diagram { get; set; }                // Mermaid source
    public string? DiagramImageUrl { get; set; }        // server-rendered PNG of Diagram, when rendering succeeded
    public required string PayloadHash { get; set; }    // for idempotency conflict detection
    public Guid? PublishedByCredentialId { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public int MatchedEndpoints { get; set; }
    public List<Delivery> Deliveries { get; set; } = [];

    public IReadOnlyList<string> Tags => TagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
}

public static class DeliveryStatus
{
    public const string Queued = "queued";      // endpoint offline; eligible for replay on reconnect
    public const string Sent = "sent";          // sent over a live connection
    public const string Pushed = "pushed";      // accepted by the push provider (FCM); display is unknown until the endpoint acks
    public const string Acked = "acked";        // endpoint confirmed display
    public const string Expired = "expired";    // replay window elapsed
}

public sealed class Delivery
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid NotificationId { get; set; }
    public Guid EndpointRef { get; set; }
    public required string Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? AckedAt { get; set; }
    public Notification Notification { get; set; } = null!;
    public Endpoint Endpoint { get; set; } = null!;
}

public static class PairingStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Consumed = "consumed";
}

/// <summary>A pending device-pairing request (OAuth device authorization grant shape).</summary>
public sealed class DevicePairing
{
    public Guid Id { get; set; }
    public required string DeviceCodeHash { get; set; }   // secret the device polls with
    public required string UserCode { get; set; }         // short code the person types/sees, unique while pending
    public required string EndpointId { get; set; }
    public required string DeviceName { get; set; }
    public required string Platform { get; set; }
    public string? AgentVersion { get; set; }
    public required string Client { get; set; }           // "agent" | "cli" | "mobile"
    public required string Status { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? ApprovedByAccountId { get; set; }
    public Guid? CredentialId { get; set; }
    public string? IssuedToken { get; set; }              // held briefly until the device polls it, then cleared
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }
}

/// <summary>
/// A non-human principal (AI agent, automation) in a workspace. Holds the policy its keys inherit: fine-grained
/// scopes, a hashtag allowlist, a URL policy and an hourly publish quota. Keys are <see cref="Credential"/> rows of
/// kind "service" pointing back here; disabling the account suspends them all at once.
/// </summary>
public sealed class ServiceAccount
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Scopes { get; set; }         // comma separated ServiceScopes
    public required string AllowedTags { get; set; }    // comma separated glob patterns, normalised like tags ("agent-*")
    public string? AllowedUrlHosts { get; set; }        // comma separated host names; null = any https host
    public int HourlyPublishLimit { get; set; } = 60;
    public bool Enabled { get; set; } = true;
    public Guid? CreatedByAccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    public IReadOnlyList<string> ScopeList => Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public IReadOnlyList<string> AllowedTagList => AllowedTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public IReadOnlyList<string> AllowedUrlHostList => (AllowedUrlHosts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static class AuditActions
{
    public const string ServiceAccountCreated = "service_account.created";
    public const string ServiceAccountUpdated = "service_account.updated";
    public const string ServiceAccountEnabled = "service_account.enabled";
    public const string ServiceAccountDisabled = "service_account.disabled";
    public const string KeyIssued = "key.issued";
    public const string KeyRevoked = "key.revoked";
    public const string Published = "notification.published";
    public const string PublishRefused = "notification.refused";
    public const string SubscriptionsChanged = "subscriptions.changed";
    public const string OAuthGranted = "oauth.granted";
    public const string OAuthRefreshed = "oauth.refreshed";
    public const string OAuthRevoked = "oauth.revoked";
}

/// <summary>An audit row for service-account activity and credential management. Never contains a secret.</summary>
public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ServiceAccountId { get; set; }
    public Guid? CredentialId { get; set; }
    public Guid? AccountId { get; set; }                 // the signed-in person, for dashboard actions
    public required string Action { get; set; }
    public string? Target { get; set; }                  // e.g. notification id, key hint, tag list
    public string? Detail { get; set; }                  // short human-readable detail
    public string? Source { get; set; }                  // "mcp" | "api" | "dashboard"
    public string? Ip { get; set; }
    public DateTimeOffset At { get; set; }
}

// ---- OAuth 2.1 authorization server (phase 2: hosted assistants such as claude.ai connect through consent) ----

/// <summary>A dynamically registered OAuth client (RFC 7591). Public clients use PKCE only; confidential ones add a secret.</summary>
public sealed class OAuthClient
{
    public required string Id { get; set; }               // "hrc_…"
    public required string Name { get; set; }
    public required string RedirectUris { get; set; }     // newline separated, exact-match
    public required string AuthMethod { get; set; }       // "none" | "client_secret_post"
    public string? SecretHash { get; set; }
    public string? ClientUri { get; set; }
    public string? LogoUri { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    public IReadOnlyList<string> RedirectUriList => RedirectUris.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>A person's consent for one client in one workspace. Backed by a service account that carries the policy.</summary>
public sealed class OAuthGrant
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AccountId { get; set; }
    public required string ClientId { get; set; }
    public Guid ServiceAccountId { get; set; }
    public required string Scopes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>A one-time authorization code bound to the client, redirect URI and PKCE challenge.</summary>
public sealed class OAuthCode
{
    public Guid Id { get; set; }
    public required string CodeHash { get; set; }
    public Guid GrantId { get; set; }
    public required string ClientId { get; set; }
    public required string RedirectUri { get; set; }
    public required string CodeChallenge { get; set; }
    public string? Resource { get; set; }
    public required string Scopes { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>A rotating refresh token; each use revokes it and its access credential and issues a new pair.</summary>
public sealed class OAuthRefreshToken
{
    public Guid Id { get; set; }
    public required string Hash { get; set; }
    public Guid GrantId { get; set; }
    public Guid AccessCredentialId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}

/// <summary>A self-service password-reset link. Only the SHA-256 verifier of the emailed token is stored; single use, short lived.</summary>
public sealed class PasswordReset
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public required string TokenHash { get; set; }      // hex SHA-256 of the token in the link, unique
    public string? RequestIp { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }     // set on use, or when a newer link supersedes this one
}
