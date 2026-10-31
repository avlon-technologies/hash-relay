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
    public List<Subscription> Subscriptions { get; set; } = [];
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
    public const string Sent = "sent";          // pushed over a live connection
    public const string Acked = "acked";        // agent confirmed display
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
    public required string Client { get; set; }           // "agent" | "cli"
    public required string Status { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? ApprovedByAccountId { get; set; }
    public Guid? CredentialId { get; set; }
    public string? IssuedToken { get; set; }              // held briefly until the device polls it, then cleared
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }
}
