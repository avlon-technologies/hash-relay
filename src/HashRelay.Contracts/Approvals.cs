namespace HashRelay.Contracts;

// Actionable approvals, HTTP API v1 (docs/ACTIONABLE-APPROVALS.md). An approval is a durable human decision the
// originating system reads back; HashRelay never performs the approved operation.

/// <summary>Lifecycle: created <see cref="Pending"/>, leaves it exactly once, and every other status is terminal and immutable.</summary>
public static class ApprovalStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public static readonly string[] Terminal = [Approved, Rejected, Expired, Cancelled];
    public static bool IsTerminal(string status) => Terminal.Contains(status);
}

/// <summary>What produced a terminal outcome. Only <see cref="Decision"/> involves a person; expiry and cancellation decide nothing.</summary>
public static class ApprovalCauses
{
    public const string Decision = "decision";
    public const string Expiry = "expiry";
    public const string Cancellation = "cancellation";
}

public static class ApprovalDecisions
{
    public const string Approve = "approve";
    public const string Reject = "reject";
}

public static class ApprovalLimits
{
    public const int SubjectMax = 120;
    public const int DescriptionMax = 4000;
    public const int NoteMax = 500;
    public const int CorrelationIdMax = 128;
    public const int IdempotencyKeyMax = 128;
    public const int ApproversMax = 10;
    public static readonly TimeSpan MinLifetime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);
}

/// <summary>
/// <c>POST /api/v1/approvals</c> with an <c>Idempotency-Key</c> header. The workspace and the requester come from the
/// authenticated credential; nothing here can name them. <paramref name="TargetUserIds"/> are the designated approvers: workspace member account ids.
/// <paramref name="Tags"/>, when given, address the ordinary notification that links to the approval.
/// </summary>
public sealed record CreateApprovalRequest(
    string Subject,
    IReadOnlyList<Guid> TargetUserIds,
    DateTimeOffset ExpiresAt,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    string? RequesterCorrelationId = null);

/// <summary><c>POST /api/v1/approvals/{id}/decision</c>: a signed-in designated approver's requested decision.</summary>
public sealed record ApprovalDecisionRequest(string Decision, string? Note = null);

public sealed record ApprovalRequester(Guid Id, string Name, string Type);

/// <summary>How the approval left Pending. <see cref="DecidedBy"/> is set only when <see cref="Cause"/> is a decision.</summary>
public sealed record ApprovalResolution(string Cause, DateTimeOffset ResolvedAt, Guid? DecidedBy, string? DecidedByName, string? Note);

/// <summary>
/// The authoritative approval record. <see cref="Proceed"/> is true only for <see cref="ApprovalStatuses.Approved"/>: the
/// requester may then run its own operation after re-checking its own preconditions; every other status means do not.
/// </summary>
public sealed record ApprovalResource(
    Guid Id,
    string Status,
    bool Proceed,
    int Version,
    string Subject,
    string? Description,
    IReadOnlyList<Guid> TargetUserIds,
    IReadOnlyList<string> Tags,
    string? RequesterCorrelationId,
    ApprovalRequester Requester,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ApprovalResolution? Resolution,
    Guid? NotificationId,
    string DetailUrl);

/// <summary>The <c>approval.resolved</c> event body (ADL record ApprovalOutcome): identity and outcome, no request content.</summary>
public sealed record ApprovalOutcome(
    Guid ApprovalId,
    Guid WorkspaceId,
    Guid RequesterId,
    string? CorrelationId,
    string Status,
    string Cause,
    int Version,
    DateTimeOffset ResolvedAt,
    Guid? DecidedBy);
