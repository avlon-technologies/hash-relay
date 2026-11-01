namespace HashRelay.Server.Data;

/// <summary>
/// ApprovalStore (ADL HashRelay.Approvals.DecisionManagement): the sole authority for an approval's status. Belongs to
/// exactly one workspace and one requesting credential, both taken from authentication. Leaves Pending once, by a
/// compare-and-set on (Id, TenantId, Status = pending, ExpiresAt, Version); terminal rows are never changed again.
/// </summary>
public sealed class Approval
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RequesterCredentialId { get; set; }
    /// <summary>Set when the requester is a service account: any of its keys may read or cancel what another created.</summary>
    public Guid? RequesterServiceAccountId { get; set; }
    /// <summary>Publisher attribution of the requester, snapshotted from the credential at creation (see <c>PublisherIdentity</c>).</summary>
    public Guid RequesterPublisherId { get; set; }
    public required string RequesterName { get; set; }
    public required string RequesterType { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string PayloadHash { get; set; }
    public string? RequesterCorrelationId { get; set; }
    public required string Subject { get; set; }
    /// <summary>Requester-written, untrusted text: shown as text, never as markup or instructions.</summary>
    public string? Description { get; set; }
    /// <summary>Designated approvers: comma-separated member account ids, normalised and sorted.</summary>
    public required string ApproverIdsCsv { get; set; }
    public required string TagsCsv { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolutionCause { get; set; }
    public Guid? DecidedByAccountId { get; set; }
    public string? DecidedByName { get; set; }
    public string? DecisionNote { get; set; }
    public string? DecisionIdempotencyKey { get; set; }
    /// <summary>1 at creation; each transition increments it. Part of the compare-and-set and of every event.</summary>
    public int Version { get; set; }
    /// <summary>Reserved at creation so publishing the linked notification is idempotent under retries.</summary>
    public Guid NotificationId { get; set; }
    /// <summary>Set once the linked notification has been accepted by routing.</summary>
    public DateTimeOffset? NotificationPublishedAt { get; set; }
    /// <summary>Absolute HashRelay URL of the decision page, fixed at creation (the publisher supplies no link).</summary>
    public required string DetailUrl { get; set; }

    public IReadOnlyList<Guid> ApproverIds => ApproverIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();
    public IReadOnlyList<string> Tags => TagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
}

public static class ApprovalAuditActions
{
    public const string Created = "created";
    public const string NotificationPublished = "notification.published";
    public const string NotificationSkipped = "notification.skipped";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public const string OutcomeAvailable = "outcome.available";
}

public static class ApprovalActorKinds
{
    public const string Credential = "credential";
    public const string Account = "account";
    public const string System = "system";
}

/// <summary>Immutable audit of one approval: who did what, at which version. Never holds a credential or the description.</summary>
public sealed class ApprovalAudit
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ApprovalId { get; set; }
    public required string Action { get; set; }
    public int Version { get; set; }
    public required string ActorKind { get; set; }
    public Guid? ActorId { get; set; }
    public string? CorrelationId { get; set; }
    public string? RequestId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset At { get; set; }
}

public static class ApprovalOutboxKinds
{
    /// <summary>Publish the ordinary notification that links to the approval.</summary>
    public const string Notification = "notification.publish";
    /// <summary>The ApprovalResolved event, consumed by ApprovalOutcomeReader.</summary>
    public const string Resolved = "approval.resolved";
}

/// <summary>
/// Transactional outbox: written in the same transaction as the state it reports, dispatched after commit with bounded
/// retries. Unique per (approval, kind, version), so a transition cannot enqueue its event twice.
/// </summary>
public sealed class ApprovalOutbox
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ApprovalId { get; set; }
    public required string Kind { get; set; }
    public int Version { get; set; }
    public string? Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// DecisionGrantStore (ADL HashRelay.Approvals.DecisionManagement): a person's explicit, revocable grant letting one
/// paired device submit that person's approval decisions in one workspace. Requested by the device, granted by the
/// signed-in person who paired it, collected once by the device. Only verifiers of the poll code and the grant secret
/// are kept, except <see cref="IssuedSecret"/>, held between granting and the device's next poll, then cleared.
/// </summary>
public sealed class DecisionGrant
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>The paired device credential this grant is bound to; it must still be active at decision time.</summary>
    public Guid DeviceCredentialId { get; set; }
    public required string DeviceName { get; set; }
    /// <summary>The person whose decisions the device may submit. Set at grant time; must be the person who paired the device.</summary>
    public Guid? AccountId { get; set; }
    public required string Status { get; set; }
    public required string PollCodeHash { get; set; }
    public required string UserCode { get; set; }
    public string? SecretHash { get; set; }
    public string? IssuedSecret { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Until granted: when the request lapses. Once granted: when the grant itself expires.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? GrantedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public static class DecisionGrantStatuses
{
    public const string Pending = "pending";
    public const string Granted = "granted";   // granted; the device has not collected the secret yet
    public const string Active = "active";     // collected and usable
    public const string Denied = "denied";
    public const string Revoked = "revoked";
}
