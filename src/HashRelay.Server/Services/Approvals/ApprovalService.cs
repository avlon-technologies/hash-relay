using System.Text.Json;
using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Services.Approvals;

/// <summary>The authenticated requesting application, derived from its credential's claims; never from a request body.</summary>
public sealed record ApprovalRequesterPrincipal(Guid TenantId, Guid CredentialId, Guid? ServiceAccountId, PublisherIdentity Publisher, ServicePolicy? Policy);

/// <summary>A workspace member who may be named in <c>targetUserIds</c>.</summary>
public sealed record EligibleApprover(Guid AccountId, string DisplayName, string Role, bool AuthorizedThisCredential);

/// <summary>
/// ApprovalService (ADL HashRelay.Approvals.DecisionManagement.ApprovalService). Handles RequestApproval, DecideApproval
/// and CancelApproval; is the only writer of ApprovalStore; publishes ApprovalResolved through the transactional outbox.
/// <list type="bullet">
/// <item>Every transition is one compare-and-set from Pending, in one transaction with its audit entry and its outbox
/// record, so state, audit and event commit together or not at all, and two contenders cannot both win.</item>
/// <item>A decision is valid only while <c>serverNow &lt; expiresAt</c>; at or after expiry the approval is Expired.</item>
/// <item>Deciding is a signed-in human's act: the caller passes a verified account id; membership, designation and
/// expiry are re-checked at decision time. No credential, service account or device token can decide.</item>
/// <item>Notification publication is a separate, retried outbox step: its failure never changes approval state.</item>
/// </list>
/// HashRelay never performs the approved operation.
/// </summary>
public sealed class ApprovalService(RelayDbContext db, TimeProvider clock, PublicBase publicBase, IServiceScopeFactory scopes,
    ILogger<ApprovalService> log)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxOutboxAttempts = 10;

    /// <summary>Every workspace role may be designated in v1; the designation itself is the permission (approvals:decide).</summary>
    public static bool RoleMayDecide(string role) => role is Roles.Owner or Roles.Admin or Roles.Member;

    // ------------------------------------------------------------------ RequestApproval

    public async Task<(ApprovalResource Resource, bool Created)> CreateAsync(ApprovalRequesterPrincipal requester, string? idempotencyKey,
        CreateApprovalRequest req, string? requestId, CancellationToken ct)
    {
        var key = ValidateIdempotencyKey(idempotencyKey, required: true)!;
        var now = clock.GetUtcNow();
        var v = Validate(req, now);

        // A service account is bounded by its hashtag allowlist for the linked notification, exactly as for publishing.
        if (requester.Policy is { } policy && v.Tags.Count > 0)
        {
            var disallowed = policy.DisallowedTags(v.Tags);
            if (disallowed.Count > 0)
                throw ApiException.Forbidden($"This service account may not address {Contracts.Tags.DisplayAll(disallowed)}.");
        }

        var payloadHash = Secrets.Sha256Hex(JsonSerializer.Serialize(new
        {
            v.Subject, v.Description, Approvers = v.ApproverIds, ExpiresAt = v.ExpiresAt.UtcTicks, v.Tags, v.CorrelationId,
        }));

        var existing = await FindByKeyAsync(requester, key, ct);
        if (existing is not null) return (await ReplayAsync(existing, payloadHash, ct), false);

        // Designated approvers must be members of THIS workspace, resolved server-side; a foreign or unknown id is
        // refused with the same message, so the response says nothing about other workspaces.
        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.TenantId == requester.TenantId && v.ApproverIds.Contains(m.AccountId))
            .Select(m => new { m.AccountId, m.Role }).ToListAsync(ct);
        var eligible = members.Where(m => RoleMayDecide(m.Role)).Select(m => m.AccountId).ToHashSet();
        var unknown = v.ApproverIds.Where(id => !eligible.Contains(id)).ToList();
        if (unknown.Count > 0)
            throw ApiException.Validation($"targetUserIds must be members of this workspace: {string.Join(", ", unknown)} is not.", "targetUserIds");

        var id = Guid.NewGuid();
        var approval = new Approval
        {
            Id = id, TenantId = requester.TenantId, RequesterCredentialId = requester.CredentialId,
            RequesterServiceAccountId = requester.ServiceAccountId, RequesterPublisherId = requester.Publisher.Id,
            RequesterName = Trim(requester.Publisher.Name, 80)!, RequesterType = requester.Publisher.Type,
            IdempotencyKey = key, PayloadHash = payloadHash, RequesterCorrelationId = v.CorrelationId,
            Subject = v.Subject, Description = v.Description, ApproverIdsCsv = string.Join(',', v.ApproverIds),
            TagsCsv = string.Join(',', v.Tags), Status = ApprovalStatuses.Pending, CreatedAt = now, ExpiresAt = v.ExpiresAt,
            Version = 1, NotificationId = Guid.NewGuid(), DetailUrl = publicBase.Absolute($"/approvals/{id}"),
        };
        db.Approvals.Add(approval);
        db.ApprovalAudits.Add(Audit(approval, ApprovalAuditActions.Created, ApprovalActorKinds.Credential, requester.CredentialId, requestId,
            $"{v.ApproverIds.Count} approver(s); expires {v.ExpiresAt:u}"));
        ApprovalOutbox? intent = null;
        if (v.Tags.Count > 0)
        {
            // The publication intent commits with the approval: if publishing later fails, the approval still exists
            // and the intent is retried; if the process dies here, nothing half-exists.
            intent = new ApprovalOutbox
            {
                Id = Guid.NewGuid(), TenantId = approval.TenantId, ApprovalId = id, Kind = ApprovalOutboxKinds.Notification,
                Version = 1, CreatedAt = now, NextAttemptAt = now,
            };
            db.ApprovalOutbox.Add(intent);
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race on the same Idempotency-Key: the winner's row is the answer.
            db.ChangeTracker.Clear();
            var winner = await FindByKeyAsync(requester, key, ct);
            if (winner is null) throw;
            return (await ReplayAsync(winner, payloadHash, ct), false);
        }
        db.ChangeTracker.Clear();
        log.LogInformation("Approval {ApprovalId} requested in tenant {TenantId} by credential {CredentialId}", id, approval.TenantId, requester.CredentialId);

        if (intent is not null) await TryProcessInFreshScopeAsync(intent.Id, ct);
        return (await ToResourceAsync(id, ct), true);
    }

    /// <summary>
    /// The workspace members a requester may designate: the same rule CreateAsync enforces. Ids and display names only
    /// (no emails); <c>AuthorizedThisCredential</c> marks the person who created the requester's service account, which
    /// for an OAuth-connected AI app is the person who connected it.
    /// </summary>
    public async Task<IReadOnlyList<EligibleApprover>> EligibleApproversAsync(ApprovalRequesterPrincipal requester, CancellationToken ct)
    {
        Guid? createdBy = requester.ServiceAccountId is { } sa
            ? await db.ServiceAccounts.AsNoTracking().Where(s => s.Id == sa && s.TenantId == requester.TenantId).Select(s => s.CreatedByAccountId).FirstOrDefaultAsync(ct)
            : null;
        var rows = await db.Memberships.AsNoTracking().Where(m => m.TenantId == requester.TenantId)
            .Select(m => new { m.AccountId, m.Role, m.Account.DisplayName }).ToListAsync(ct);
        return rows.Where(m => RoleMayDecide(m.Role))
            .Select(m => new EligibleApprover(m.AccountId, m.DisplayName, m.Role, m.AccountId == createdBy))
            .OrderByDescending(m => m.AuthorizedThisCredential).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).Take(100).ToList();
    }

    private Task<Approval?> FindByKeyAsync(ApprovalRequesterPrincipal requester, string key, CancellationToken ct)
        => db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == requester.TenantId
            && a.RequesterCredentialId == requester.CredentialId && a.IdempotencyKey == key, ct);

    private async Task<ApprovalResource> ReplayAsync(Approval existing, string payloadHash, CancellationToken ct)
    {
        if (existing.PayloadHash != payloadHash)
            throw ApiException.Conflict("An approval with this Idempotency-Key already exists with a different request.");
        return await GetCurrentAsync(existing, ct);
    }

    // ------------------------------------------------------------------ requester retrieval and CancelApproval

    /// <summary>The authoritative record for its requester. Anyone else, in any workspace, gets 404: existence is not disclosed.</summary>
    public async Task<ApprovalResource> GetForRequesterAsync(ApprovalRequesterPrincipal requester, Guid id, CancellationToken ct)
        => await GetCurrentAsync(await RequireForRequesterAsync(requester, id, ct), ct);

    public async Task<ApprovalResource> CancelAsync(ApprovalRequesterPrincipal requester, Guid id, string? requestId, CancellationToken ct)
    {
        var a = await RequireForRequesterAsync(requester, id, ct);
        var now = clock.GetUtcNow();
        if (a.Status == ApprovalStatuses.Pending && now >= a.ExpiresAt)
        {
            await ExpireAsync(a, now, requestId, ct);
            throw StateConflict("The approval expired before it was cancelled.", await LoadAsync(id, ct));
        }
        if (a.Status == ApprovalStatuses.Cancelled) return await ToResourceAsync(id, ct);
        if (a.Status != ApprovalStatuses.Pending) throw StateConflict("The approval is already resolved and cannot be cancelled.", a);

        var won = await TransitionAsync(a, ApprovalStatuses.Cancelled, ApprovalCauses.Cancellation, now, beforeExpiry: true,
            decider: null, note: null, decisionKey: null,
            Audit(a, ApprovalAuditActions.Cancelled, ApprovalActorKinds.Credential, requester.CredentialId, requestId, null), ct);
        var current = await LoadAsync(id, ct);
        if (won || current.Status == ApprovalStatuses.Cancelled) return ToResource(current);
        if (current.Status == ApprovalStatuses.Pending && now >= current.ExpiresAt)
        {
            await ExpireAsync(current, now, requestId, ct);
            current = await LoadAsync(id, ct);
        }
        throw StateConflict("The approval was resolved before it could be cancelled.", current);
    }

    private async Task<Approval> RequireForRequesterAsync(ApprovalRequesterPrincipal requester, Guid id, CancellationToken ct)
    {
        var a = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == requester.TenantId, ct);
        var mine = a is not null && (a.RequesterCredentialId == requester.CredentialId
            || (a.RequesterServiceAccountId is not null && a.RequesterServiceAccountId == requester.ServiceAccountId));
        return mine ? a! : throw ApiException.NotFound("No such approval.");
    }

    // ------------------------------------------------------------------ DecideApproval (human only)

    /// <summary>The approval as a signed-in person may see it: only a current member of its workspace who is a designated approver.</summary>
    public async Task<Approval?> FindForApproverAsync(Guid accountId, Guid id, CancellationToken ct)
    {
        var a = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null || !await IsEligibleApproverAsync(accountId, a, ct)) return null;
        if (a.Status == ApprovalStatuses.Pending && clock.GetUtcNow() >= a.ExpiresAt)
        {
            await ExpireAsync(a, clock.GetUtcNow(), null, ct);
            a = await LoadAsync(id, ct);
        }
        return a;
    }

    /// <summary>Pending approvals designating this person, across the workspaces they currently belong to.</summary>
    public async Task<List<Approval>> PendingForApproverAsync(Guid accountId, CancellationToken ct)
    {
        var tenants = await db.Memberships.AsNoTracking().Where(m => m.AccountId == accountId).Select(m => m.TenantId).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var marker = accountId.ToString();
        var rows = await db.Approvals.AsNoTracking()
            .Where(a => tenants.Contains(a.TenantId) && a.Status == ApprovalStatuses.Pending && a.ExpiresAt > now && a.ApproverIdsCsv.Contains(marker))
            .OrderBy(a => a.ExpiresAt).Take(100).ToListAsync(ct);
        return rows.Where(a => a.ApproverIds.Contains(accountId)).ToList();
    }

    public async Task<ApprovalResource> DecideAsync(Guid accountId, Guid id, ApprovalDecisionRequest req, string? idempotencyKey, string? requestId, CancellationToken ct)
    {
        var key = ValidateIdempotencyKey(idempotencyKey, required: false);
        var decision = (req.Decision ?? "").Trim().ToLowerInvariant();
        if (decision is not (ApprovalDecisions.Approve or ApprovalDecisions.Reject))
            throw ApiException.Validation("decision must be \"approve\" or \"reject\".", "decision");
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        if (note is not null && note.Length > ApprovalLimits.NoteMax)
            throw ApiException.Validation($"note must be at most {ApprovalLimits.NoteMax} characters.", "note");
        var target = decision == ApprovalDecisions.Approve ? ApprovalStatuses.Approved : ApprovalStatuses.Rejected;

        var a = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        // Membership is read live, not from the session: a person removed from the workspace sees nothing.
        var role = a is null ? null : await db.Memberships.AsNoTracking()
            .Where(m => m.TenantId == a.TenantId && m.AccountId == accountId).Select(m => m.Role).FirstOrDefaultAsync(ct);
        if (a is null || role is null) throw ApiException.NotFound("No such approval.");
        if (!RoleMayDecide(role) || !a.ApproverIds.Contains(accountId))
            throw ApiException.Forbidden("You are not a designated approver of this request.");

        // Exact retry of a decision that already committed: the prior outcome, no new transition.
        if (key is not null && a.DecisionIdempotencyKey == key && a.DecidedByAccountId == accountId)
        {
            if (a.Status == target) return ToResource(a);
            throw StateConflict("This Idempotency-Key was used for a different decision.", a);
        }

        var now = clock.GetUtcNow();
        if (a.Status == ApprovalStatuses.Pending && now >= a.ExpiresAt)
        {
            await ExpireAsync(a, now, requestId, ct);
            throw StateConflict("The approval has expired.", await LoadAsync(id, ct));
        }
        if (a.Status != ApprovalStatuses.Pending) throw StateConflict("The approval is already resolved.", a);

        var name = await db.Accounts.AsNoTracking().Where(x => x.Id == accountId).Select(x => x.DisplayName).FirstAsync(ct);
        var won = await TransitionAsync(a, target, ApprovalCauses.Decision, now, beforeExpiry: true, decider: (accountId, name), note, key,
            Audit(a, target == ApprovalStatuses.Approved ? ApprovalAuditActions.Approved : ApprovalAuditActions.Rejected,
                ApprovalActorKinds.Account, accountId, requestId, null), ct);
        var current = await LoadAsync(id, ct);
        if (won) return ToResource(current);
        // Lost: never revert, report the winner. If nobody won, the boundary passed; expiry is the outcome.
        if (current.Status == ApprovalStatuses.Pending && now >= current.ExpiresAt)
        {
            await ExpireAsync(current, now, requestId, ct);
            current = await LoadAsync(id, ct);
        }
        throw StateConflict("Another decision was recorded first.", current);
    }

    private async Task<bool> IsEligibleApproverAsync(Guid accountId, Approval a, CancellationToken ct)
    {
        var role = await db.Memberships.AsNoTracking().Where(m => m.TenantId == a.TenantId && m.AccountId == accountId).Select(m => m.Role).FirstOrDefaultAsync(ct);
        return role is not null && RoleMayDecide(role) && a.ApproverIds.Contains(accountId);
    }

    // ------------------------------------------------------------------ expiry

    /// <summary>Materialises expiry (lazy on read/decide/cancel, and by the sweep). The database condition is definitive.</summary>
    private Task<bool> ExpireAsync(Approval a, DateTimeOffset now, string? requestId, CancellationToken ct)
        => TransitionAsync(a, ApprovalStatuses.Expired, ApprovalCauses.Expiry, now, beforeExpiry: false, decider: null, note: null, decisionKey: null,
            Audit(a, ApprovalAuditActions.Expired, ApprovalActorKinds.System, null, requestId, null), ct);

    /// <summary>Expires every due Pending approval (batch), each with its own audit and resolved event.</summary>
    public async Task<int> ExpireDueAsync(int limit, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await db.Approvals.AsNoTracking().Where(a => a.Status == ApprovalStatuses.Pending && a.ExpiresAt <= now)
            .OrderBy(a => a.ExpiresAt).Take(limit).ToListAsync(ct);
        var expired = 0;
        foreach (var a in due) if (await ExpireAsync(a, now, null, ct)) expired++;
        return expired;
    }

    // ------------------------------------------------------------------ the compare-and-set

    /// <summary>
    /// Pending → <paramref name="status"/> iff the row is still Pending at the version read, and (for a decision or
    /// cancellation) still before expiry, or (for expiry) at or past it. The audit entry and the ApprovalResolved outbox
    /// record are inserted in the same transaction. Returns false when another transition got there first.
    /// </summary>
    private async Task<bool> TransitionAsync(Approval a, string status, string cause, DateTimeOffset now, bool beforeExpiry,
        (Guid Id, string Name)? decider, string? note, string? decisionKey, ApprovalAudit audit, CancellationToken ct)
    {
        var next = a.Version + 1;
        var deciderId = decider?.Id;
        var deciderName = decider?.Name;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var q = db.Approvals.Where(x => x.Id == a.Id && x.TenantId == a.TenantId && x.Status == ApprovalStatuses.Pending && x.Version == a.Version);
        q = beforeExpiry ? q.Where(x => x.ExpiresAt > now) : q.Where(x => x.ExpiresAt <= now);
        var rows = await q.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, status)
            .SetProperty(x => x.Version, next)
            .SetProperty(x => x.ResolvedAt, now)
            .SetProperty(x => x.ResolutionCause, cause)
            .SetProperty(x => x.DecidedByAccountId, deciderId)
            .SetProperty(x => x.DecidedByName, deciderName)
            .SetProperty(x => x.DecisionNote, note)
            .SetProperty(x => x.DecisionIdempotencyKey, decisionKey), ct);
        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        audit.Version = next;
        db.ApprovalAudits.Add(audit);
        var outcome = new ApprovalOutcome(a.Id, a.TenantId, a.RequesterPublisherId, a.RequesterCorrelationId, status, cause, next, now, deciderId);
        db.ApprovalOutbox.Add(new ApprovalOutbox
        {
            Id = Guid.NewGuid(), TenantId = a.TenantId, ApprovalId = a.Id, Kind = ApprovalOutboxKinds.Resolved, Version = next,
            Payload = JsonSerializer.Serialize(outcome, Json), CreatedAt = now, NextAttemptAt = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        log.LogInformation("Approval {ApprovalId} {Status} by {Cause} at version {Version}", a.Id, status, cause, next);
        return true;
    }

    // ------------------------------------------------------------------ outbox

    /// <summary>Dispatches due outbox records, each in its own scope. Returns how many were completed.</summary>
    public async Task<int> DispatchDueAsync(int limit, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await db.ApprovalOutbox.AsNoTracking().Where(o => o.ProcessedAt == null && o.FailedAt == null && o.NextAttemptAt <= now)
            .OrderBy(o => o.CreatedAt).Take(limit).Select(o => o.Id).ToListAsync(ct);
        var done = 0;
        foreach (var id in due) if (await TryProcessInFreshScopeAsync(id, ct)) done++;
        return done;
    }

    private async Task<bool> TryProcessInFreshScopeAsync(Guid outboxId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApprovalOutboxProcessor>().ProcessAsync(outboxId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Approval outbox {OutboxId} dispatch failed; it stays pending for retry", outboxId);
            return false;
        }
    }

    internal static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(600, 5 * Math.Pow(2, Math.Max(0, attempts - 1))));
    internal static int MaxAttempts => MaxOutboxAttempts;

    // ------------------------------------------------------------------ helpers

    private async Task<ApprovalResource> GetCurrentAsync(Approval a, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (a.Status == ApprovalStatuses.Pending && now >= a.ExpiresAt)
        {
            await ExpireAsync(a, now, null, ct);
            a = await LoadAsync(a.Id, ct);
        }
        return ToResource(a);
    }

    private Task<Approval> LoadAsync(Guid id, CancellationToken ct) => db.Approvals.AsNoTracking().FirstAsync(x => x.Id == id, ct);

    private async Task<ApprovalResource> ToResourceAsync(Guid id, CancellationToken ct) => ToResource(await LoadAsync(id, ct));

    public static ApprovalResource ToResource(Approval a) => new(
        a.Id, a.Status, a.Status == ApprovalStatuses.Approved, a.Version, a.Subject, a.Description, a.ApproverIds, a.Tags,
        a.RequesterCorrelationId, new ApprovalRequester(a.RequesterPublisherId, a.RequesterName, a.RequesterType),
        a.CreatedAt, a.ExpiresAt,
        a.ResolutionCause is null ? null : new ApprovalResolution(a.ResolutionCause, a.ResolvedAt!.Value, a.DecidedByAccountId, a.DecidedByName, a.DecisionNote),
        a.NotificationPublishedAt is null ? null : a.NotificationId, a.DetailUrl);

    private static ApiException StateConflict(string message, Approval current)
        => new(409, ErrorCodes.Conflict, message, new Dictionary<string, string[]>
        {
            ["status"] = [current.Status], ["version"] = [current.Version.ToString()],
        });

    internal static ApprovalAudit Audit(Approval a, string action, string actorKind, Guid? actorId, string? requestId, string? detail) => new()
    {
        Id = Guid.NewGuid(), TenantId = a.TenantId, ApprovalId = a.Id, Action = action, Version = a.Version, ActorKind = actorKind,
        ActorId = actorId, CorrelationId = a.RequesterCorrelationId, RequestId = Trim(requestId, 64), Detail = Trim(detail, 300), At = DateTimeOffset.UtcNow,
    };

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    private static string? ValidateIdempotencyKey(string? raw, bool required)
    {
        var key = raw?.Trim();
        if (string.IsNullOrEmpty(key))
            return required ? throw ApiException.Validation("Send an Idempotency-Key header (1–128 visible characters).", "Idempotency-Key") : null;
        if (key.Length > ApprovalLimits.IdempotencyKeyMax || key.Any(c => c < 0x21 || c > 0x7e))
            throw ApiException.Validation("Idempotency-Key must be 1–128 visible ASCII characters.", "Idempotency-Key");
        return key;
    }

    private sealed record Validated(string Subject, string? Description, IReadOnlyList<Guid> ApproverIds, DateTimeOffset ExpiresAt,
        IReadOnlyList<string> Tags, string? CorrelationId);

    private static Validated Validate(CreateApprovalRequest req, DateTimeOffset now)
    {
        var details = new Dictionary<string, string[]>();
        var subject = (req.Subject ?? "").Trim();
        if (subject.Length is < 1 or > ApprovalLimits.SubjectMax) details["subject"] = [$"subject must be 1–{ApprovalLimits.SubjectMax} characters."];
        var description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Replace("\r\n", "\n").Trim();
        if (description is not null && description.Length > ApprovalLimits.DescriptionMax)
            details["description"] = [$"description must be at most {ApprovalLimits.DescriptionMax} characters."];

        var approvers = (req.TargetUserIds ?? []).Distinct().OrderBy(g => g).ToList();
        if (approvers.Count == 0 || approvers.Contains(Guid.Empty)) details["targetUserIds"] = ["Name at least one approver (a workspace member account id)."];
        else if (approvers.Count > ApprovalLimits.ApproversMax) details["targetUserIds"] = [$"At most {ApprovalLimits.ApproversMax} approvers."];

        var expiresAt = req.ExpiresAt.ToUniversalTime();
        if (expiresAt < now + ApprovalLimits.MinLifetime || expiresAt > now + ApprovalLimits.MaxLifetime)
            details["expiresAt"] = ["expiresAt must be between 1 minute and 30 days from now."];

        IReadOnlyList<string> tags = [];
        if (req.Tags is { Count: > 0 })
        {
            try
            {
                tags = Contracts.Tags.NormalizeAll(req.Tags).OrderBy(t => t, StringComparer.Ordinal).ToList();
                if (tags.Count > Contracts.Tags.MaxPerPublication) details["tags"] = [$"At most {Contracts.Tags.MaxPerPublication} distinct hashtags."];
            }
            catch (ArgumentException ex) { details["tags"] = [ex.Message]; }
        }

        var correlation = string.IsNullOrWhiteSpace(req.RequesterCorrelationId) ? null : req.RequesterCorrelationId.Trim();
        if (correlation is not null && correlation.Length > ApprovalLimits.CorrelationIdMax)
            details["requesterCorrelationId"] = [$"requesterCorrelationId must be at most {ApprovalLimits.CorrelationIdMax} characters."];

        if (details.Count > 0) throw new ApiException(400, ErrorCodes.Validation, "The approval request is not valid.", details);
        return new Validated(subject, description, approvers, expiresAt, tags, correlation);
    }
}
