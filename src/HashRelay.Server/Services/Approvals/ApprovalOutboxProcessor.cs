using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Services.Approvals;

/// <summary>
/// Dispatches one transactional-outbox record after its transaction committed. Two kinds:
/// <list type="bullet">
/// <item><see cref="ApprovalOutboxKinds.Notification"/>: publish the ordinary notification that links to the approval,
/// through the existing routing path, with the notification id reserved at creation so retries are idempotent. Skipped
/// when the approval already left Pending. A failure here never changes approval state.</item>
/// <item><see cref="ApprovalOutboxKinds.Resolved"/>: ApprovalResolved, consumed by <see cref="ApprovalOutcomeReader"/>.</item>
/// </list>
/// A record is leased (NextAttemptAt pushed forward by compare-and-set) before work starts, so concurrent dispatchers
/// (the inline attempt after create and the sweep) do not both run it.
/// </summary>
public sealed class ApprovalOutboxProcessor(RelayDbContext db, TimeProvider clock, IApprovalNotificationPublisher notifier, ApprovalOutcomeReader reader,
    ILogger<ApprovalOutboxProcessor> log)
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    public async Task<bool> ProcessAsync(Guid outboxId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var row = await db.ApprovalOutbox.AsNoTracking().FirstOrDefaultAsync(o => o.Id == outboxId, ct);
        if (row is null || row.ProcessedAt is not null || row.FailedAt is not null || row.NextAttemptAt > now) return false;

        var leaseUntil = now + Lease;
        var attempts = row.Attempts + 1;
        var leased = await db.ApprovalOutbox
            .Where(o => o.Id == outboxId && o.ProcessedAt == null && o.FailedAt == null && o.NextAttemptAt == row.NextAttemptAt && o.Attempts == row.Attempts)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, leaseUntil).SetProperty(o => o.Attempts, attempts), ct);
        if (leased == 0) return false;

        try
        {
            switch (row.Kind)
            {
                case ApprovalOutboxKinds.Notification: await PublishNotificationAsync(row, ct); break;
                case ApprovalOutboxKinds.Resolved: await reader.ConsumeAsync(row, ct); break;
                default: throw new InvalidOperationException($"Unknown approval outbox kind '{row.Kind}'.");
            }
            var done = clock.GetUtcNow();
            await db.ApprovalOutbox.Where(o => o.Id == outboxId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ProcessedAt, done).SetProperty(o => o.LastError, (string?)null), ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A caller error from routing (4xx) will not fix itself; anything else is retried with backoff.
            var permanent = ex is ApiException { Status: >= 400 and < 500 and not 429 } || attempts >= ApprovalService.MaxAttempts;
            var at = clock.GetUtcNow();
            var next = at + ApprovalService.Backoff(attempts);
            var error = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            DateTimeOffset? failedAt = permanent ? at : null;
            await db.ApprovalOutbox.Where(o => o.Id == outboxId).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.NextAttemptAt, next).SetProperty(o => o.LastError, error).SetProperty(o => o.FailedAt, failedAt), CancellationToken.None);
            log.LogWarning(ex, "Approval outbox {OutboxId} ({Kind}) attempt {Attempt} failed{Permanent}", outboxId, row.Kind, attempts, permanent ? " permanently" : "");
            return false;
        }
    }

    private async Task PublishNotificationAsync(ApprovalOutbox row, CancellationToken ct)
    {
        var a = await db.Approvals.AsNoTracking().FirstAsync(x => x.Id == row.ApprovalId, ct);
        if (a.Status != ApprovalStatuses.Pending || a.Tags.Count == 0)
        {
            db.ApprovalAudits.Add(ApprovalService.Audit(a, ApprovalAuditActions.NotificationSkipped, ApprovalActorKinds.System, null, null,
                $"approval is {a.Status}"));
            await db.SaveChangesAsync(ct);
            return;
        }

        var result = await notifier.PublishAsync(a, ct);

        var now = clock.GetUtcNow();
        await db.Approvals.Where(x => x.Id == a.Id && x.NotificationPublishedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NotificationPublishedAt, now), ct);
        db.ApprovalAudits.Add(ApprovalService.Audit(a, ApprovalAuditActions.NotificationPublished, ApprovalActorKinds.System, null, null,
            $"{result.MatchedEndpoints} device(s){(result.Duplicate ? ", duplicate" : "")}"));
        await db.SaveChangesAsync(ct);
    }

}

/// <summary>Publishes the ordinary notification that links to an approval. A seam so tests can make publication fail.</summary>
public interface IApprovalNotificationPublisher
{
    Task<PublishResult> PublishAsync(Approval approval, CancellationToken ct);
}

/// <summary>Publishes through the existing routing path (subscriptions, live delivery, push, replay), unchanged.</summary>
public sealed class RoutingApprovalNotificationPublisher(RoutingService routing) : IApprovalNotificationPublisher
{
    public Task<PublishResult> PublishAsync(Approval a, CancellationToken ct)
    {
        // The notification carries only the subject and the link; the description is read on the authenticated page.
        var title = Trim("Approval requested: " + a.Subject, 120);
        var body = $"{a.RequesterName} asks for a decision before {a.ExpiresAt:yyyy-MM-dd HH:mm} UTC. Open to approve or reject.";
        var publisher = new PublisherIdentity(a.RequesterPublisherId, a.RequesterName, a.RequesterType);
        // The approval was authorised against the requester's policy at creation; the link is HashRelay's own URL,
        // which a service account's URL host allowlist was never meant to govern, so no policy is re-applied here.
        return routing.PublishAsync(a.TenantId, a.RequesterCredentialId,
            new PublishRequest(a.Tags, title, body, a.DetailUrl, Priorities.High, a.NotificationId), ct, policy: null, publisher, Channels.Rest);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>
/// ApprovalOutcomeReader (ADL): consumes ApprovalResolved. In Milestone 1 the requester reads the outcome by
/// authenticated retrieval, so the reader's job is to make the outcome available exactly once per version: it records
/// <c>outcome.available</c> in the audit, deduplicated by version, so redelivery of the same event is harmless.
/// </summary>
public sealed class ApprovalOutcomeReader(RelayDbContext db, ILogger<ApprovalOutcomeReader> log)
{
    public async Task ConsumeAsync(ApprovalOutbox row, CancellationToken ct)
    {
        var outcome = System.Text.Json.JsonSerializer.Deserialize<ApprovalOutcome>(row.Payload ?? "null", ApprovalService.Json)
            ?? throw new InvalidOperationException("approval.resolved has no payload.");
        var seen = await db.ApprovalAudits.AnyAsync(x => x.ApprovalId == outcome.ApprovalId && x.Action == ApprovalAuditActions.OutcomeAvailable
            && x.Version == outcome.Version, ct);
        if (seen) return;
        db.ApprovalAudits.Add(new ApprovalAudit
        {
            Id = Guid.NewGuid(), TenantId = outcome.WorkspaceId, ApprovalId = outcome.ApprovalId, Action = ApprovalAuditActions.OutcomeAvailable,
            Version = outcome.Version, ActorKind = ApprovalActorKinds.System, CorrelationId = outcome.CorrelationId,
            Detail = $"{outcome.Status} by {outcome.Cause}", At = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Approval {ApprovalId} outcome {Status} ({Cause}) available at version {Version}", outcome.ApprovalId, outcome.Status, outcome.Cause, outcome.Version);
    }
}

/// <summary>Periodically materialises expiry and drains the approval outbox. Interval 0 disables it (tests drive it directly).</summary>
public sealed class ApprovalMaintenanceService(IServiceScopeFactory scopes, IConfiguration config, ILogger<ApprovalMaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = config.GetValue("HashRelay:Approvals:SweepIntervalSeconds", 15);
        if (seconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var approvals = scope.ServiceProvider.GetRequiredService<ApprovalService>();
                await approvals.ExpireDueAsync(200, stoppingToken);
                await approvals.DispatchDueAsync(200, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Approval maintenance sweep failed"); }
        }
    }
}
