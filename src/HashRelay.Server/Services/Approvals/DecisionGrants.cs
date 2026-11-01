using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Services.Approvals;

/// <summary>
/// GrantDeviceDecisions / RevokeDeviceDecisions and device-submitted DecideApproval (ADL requirement
/// DeviceDecisionAuthority). The flow has the same shape as device pairing (device-authorization grant):
/// <list type="number">
/// <item>The paired device asks for a grant with its own token and gets a poll code and a short user code.</item>
/// <item>The person who paired that device signs in, reads what one-click approval means on an unlocked PC, and grants it.</item>
/// <item>The device polls with its token and the poll code and collects the grant secret exactly once.</item>
/// <item>A decision then needs the device token AND the grant secret; the person, workspace and designation are
/// re-checked by <see cref="DecideAsync"/> exactly as for a decision made on the page.</item>
/// </list>
/// A device token alone never decides; a grant secret without its device token is useless.
/// </summary>
public sealed partial class ApprovalService
{
    public async Task<DecisionGrantStart> BeginDecisionGrantAsync(Guid tenantId, Guid deviceCredentialId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var device = await ActiveDeviceCredentialAsync(tenantId, deviceCredentialId, now, ct)
            ?? throw ApiException.Forbidden("Only a paired device can ask for one-click approvals.");
        await db.DecisionGrants.Where(g => g.Status == DecisionGrantStatuses.Pending && g.ExpiresAt < now.AddHours(-1)).ExecuteDeleteAsync(ct);

        var pollCode = Secrets.NewDeviceCode();
        string userCode;
        do { userCode = Secrets.NewUserCode(); }
        while (await db.DecisionGrants.AnyAsync(g => g.UserCode == userCode && g.Status == DecisionGrantStatuses.Pending && g.ExpiresAt > now, ct));

        db.DecisionGrants.Add(new DecisionGrant
        {
            Id = Guid.NewGuid(), TenantId = tenantId, DeviceCredentialId = device.Id, DeviceName = device.Label, Status = DecisionGrantStatuses.Pending,
            PollCodeHash = Secrets.Sha256Hex(pollCode), UserCode = userCode, CreatedAt = now, ExpiresAt = now + DecisionGrants.RequestLifetime,
        });
        await db.SaveChangesAsync(ct);
        return new DecisionGrantStart(pollCode, userCode, publicBase.Absolute($"/approvals/devices?code={userCode}"),
            (int)DecisionGrants.RequestLifetime.TotalSeconds, DecisionGrants.PollIntervalSeconds);
    }

    /// <summary>Polled by the same device credential that asked; hands the grant secret over exactly once.</summary>
    public async Task<DecisionGrantPoll> PollDecisionGrantAsync(Guid tenantId, Guid deviceCredentialId, string? pollCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pollCode)) throw ApiException.Validation("pollCode is required.", "pollCode");
        var hash = Secrets.Sha256Hex(pollCode.Trim());
        var now = clock.GetUtcNow();
        var g = await db.DecisionGrants.FirstOrDefaultAsync(x => x.PollCodeHash == hash && x.TenantId == tenantId && x.DeviceCredentialId == deviceCredentialId, ct);
        if (g is null) return new DecisionGrantPoll("expired");
        switch (g.Status)
        {
            case DecisionGrantStatuses.Pending:
                return new DecisionGrantPoll(g.ExpiresAt > now ? "pending" : "expired");
            case DecisionGrantStatuses.Denied:
                return new DecisionGrantPoll("denied");
            case DecisionGrantStatuses.Granted when g.IssuedSecret is not null:
                var secret = g.IssuedSecret;
                g.IssuedSecret = null;
                g.Status = DecisionGrantStatuses.Active;
                await db.SaveChangesAsync(ct);
                var name = await db.Accounts.AsNoTracking().Where(a => a.Id == g.AccountId).Select(a => a.DisplayName).FirstOrDefaultAsync(ct);
                return new DecisionGrantPoll("granted", secret, name, g.ExpiresAt);
            default:
                return new DecisionGrantPoll("expired"); // collected already, or revoked
        }
    }

    public sealed record GrantRequestView(DecisionGrant Grant, string WorkspaceName, Guid? PairedByAccountId);

    /// <summary>A pending request by user code, for the grant page.</summary>
    public async Task<GrantRequestView?> FindDecisionGrantRequestAsync(string? userCode, CancellationToken ct)
    {
        var code = DevicePairingService.NormalizeUserCode(userCode);
        if (code.Length != 9) return null;
        var now = clock.GetUtcNow();
        var g = await db.DecisionGrants.AsNoTracking().FirstOrDefaultAsync(x => x.UserCode == code && x.Status == DecisionGrantStatuses.Pending && x.ExpiresAt > now, ct);
        if (g is null) return null;
        var device = await db.Credentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == g.DeviceCredentialId, ct);
        var workspace = await db.Tenants.AsNoTracking().Where(t => t.Id == g.TenantId).Select(t => t.Name).FirstAsync(ct);
        return new GrantRequestView(g, workspace, device?.CreatedByAccountId);
    }

    /// <summary>GrantDeviceDecisions: only the person who paired the device, while a member of its workspace, may grant it.</summary>
    public async Task GrantDeviceDecisionsAsync(Guid accountId, Guid grantId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var g = await db.DecisionGrants.FirstOrDefaultAsync(x => x.Id == grantId && x.Status == DecisionGrantStatuses.Pending && x.ExpiresAt > now, ct)
                ?? throw ApiException.NotFound("This request has expired or was already handled. Start again from the HashRelay tray menu.");
        var device = await ActiveDeviceCredentialAsync(g.TenantId, g.DeviceCredentialId, now, ct)
                     ?? throw ApiException.Forbidden("This device is no longer paired.");
        if (device.CreatedByAccountId != accountId)
            throw ApiException.Forbidden("Only the person who paired this device can let it approve on their behalf.");
        if (!await db.Memberships.AnyAsync(m => m.TenantId == g.TenantId && m.AccountId == accountId, ct))
            throw ApiException.Forbidden("You are not a member of this device's workspace.");

        // One grant per device: a new one replaces any earlier grant for the same device credential.
        await db.DecisionGrants.Where(x => x.DeviceCredentialId == g.DeviceCredentialId && x.Id != g.Id
                && (x.Status == DecisionGrantStatuses.Active || x.Status == DecisionGrantStatuses.Granted))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DecisionGrantStatuses.Revoked).SetProperty(x => x.RevokedAt, now).SetProperty(x => x.IssuedSecret, (string?)null), ct);

        var secret = DecisionGrants.SecretPrefix + Secrets.RandomString(40);
        g.AccountId = accountId;
        g.Status = DecisionGrantStatuses.Granted;
        g.SecretHash = Secrets.Sha256Hex(secret);
        g.IssuedSecret = secret;
        g.GrantedAt = now;
        g.ExpiresAt = now + DecisionGrants.GrantLifetime;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(g.TenantId, AuditActions.DecisionGrantCreated, credentialId: g.DeviceCredentialId, accountId: accountId, target: g.DeviceName,
            detail: $"one-click approvals until {g.ExpiresAt:u}", ct: ct);
        log.LogInformation("Decision grant {GrantId} created for account {AccountId} on device credential {CredentialId}", g.Id, accountId, g.DeviceCredentialId);
    }

    /// <summary>Only the person who paired the device may refuse its request, so a leaked code cannot be used to block enrolment.</summary>
    public async Task DenyDecisionGrantAsync(Guid accountId, Guid grantId, CancellationToken ct)
    {
        var mine = db.Credentials.Where(c => c.CreatedByAccountId == accountId).Select(c => c.Id);
        await db.DecisionGrants.Where(x => x.Id == grantId && x.Status == DecisionGrantStatuses.Pending && mine.Contains(x.DeviceCredentialId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DecisionGrantStatuses.Denied), ct);
    }

    /// <summary>The person's usable grants, for the revoke list.</summary>
    public async Task<List<DecisionGrant>> DecisionGrantsForAsync(Guid accountId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.DecisionGrants.AsNoTracking()
            .Where(g => g.AccountId == accountId && (g.Status == DecisionGrantStatuses.Active || g.Status == DecisionGrantStatuses.Granted) && g.ExpiresAt > now)
            .OrderByDescending(g => g.GrantedAt).ToListAsync(ct);
    }

    /// <summary>RevokeDeviceDecisions by the person, from the web.</summary>
    public async Task<bool> RevokeDecisionGrantAsync(Guid accountId, Guid grantId, CancellationToken ct)
    {
        var g = await db.DecisionGrants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == grantId && x.AccountId == accountId, ct);
        return g is not null && await RevokeAsync(g, accountId, "revoked by the person", ct);
    }

    /// <summary>RevokeDeviceDecisions by the device itself ("Turn off one-click approvals").</summary>
    public async Task RevokeDecisionGrantFromDeviceAsync(Guid tenantId, Guid deviceCredentialId, string? secret, CancellationToken ct)
    {
        var g = await FindBySecretAsync(tenantId, deviceCredentialId, secret, ct);
        if (g is not null) await RevokeAsync(g, null, "revoked from the device", ct);
    }

    private async Task<bool> RevokeAsync(DecisionGrant g, Guid? byAccount, string detail, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rows = await db.DecisionGrants.Where(x => x.Id == g.Id && x.Status != DecisionGrantStatuses.Revoked)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DecisionGrantStatuses.Revoked).SetProperty(x => x.RevokedAt, now).SetProperty(x => x.IssuedSecret, (string?)null), ct);
        if (rows > 0)
            await audit.RecordAsync(g.TenantId, AuditActions.DecisionGrantRevoked, credentialId: g.DeviceCredentialId, accountId: byAccount ?? g.AccountId, target: g.DeviceName, detail: detail, ct: ct);
        return rows > 0;
    }

    /// <summary>
    /// DecideApproval submitted by a device under a grant. The grant must be active, unexpired and bound to this very
    /// device credential and workspace; then the decision is the named person's, checked and recorded by DecideAsync
    /// (membership, designation, expiry, idempotency, compare-and-set, audit) with the approval confined to the grant's workspace.
    /// </summary>
    public async Task<ApprovalResource> DecideFromDeviceAsync(Guid tenantId, Guid deviceCredentialId, Guid? endpointRef, string? secret, Guid approvalId,
        DeviceDecisionRequest req, string? idempotencyKey, string? requestId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var g = await FindBySecretAsync(tenantId, deviceCredentialId, secret, ct);
        if (g is null || g.Status != DecisionGrantStatuses.Active || g.AccountId is null || g.ExpiresAt <= now)
            throw new ApiException(401, ErrorCodes.Unauthenticated, "One-click approval is not enabled for this device, or it was revoked or has expired. Turn it on again from the HashRelay tray menu, or decide on the approval page.",
                new Dictionary<string, string[]> { ["grant"] = [DecisionGrants.Invalid] });
        // The click must come from this approval's own notification, delivered to this device. Anyone who may publish to a
        // hashtag can send a look-alike toast linking to a real approval; its delivery belongs to a different notification.
        var genuine = endpointRef is not null && await db.Deliveries.AnyAsync(d => d.Id == req.DeliveryId && d.TenantId == g.TenantId && d.EndpointRef == endpointRef
            && db.Approvals.Any(a => a.Id == approvalId && a.TenantId == g.TenantId && a.NotificationId == d.NotificationId), ct);
        if (!genuine)
            throw ApiException.Forbidden("This button isn't from the approval's own notification on this device. Open the approval in HashRelay to decide.");
        await db.DecisionGrants.Where(x => x.Id == g.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, now), ct);
        return await DecideAsync(g.AccountId.Value, approvalId, new ApprovalDecisionRequest(req.Decision, req.Note), idempotencyKey, requestId, ct, tenantScope: g.TenantId,
            via: $"one-click on {g.DeviceName}");
    }

    private async Task<DecisionGrant?> FindBySecretAsync(Guid tenantId, Guid deviceCredentialId, string? secret, CancellationToken ct)
    {
        var s = secret?.Trim();
        if (string.IsNullOrEmpty(s) || !s.StartsWith(DecisionGrants.SecretPrefix, StringComparison.Ordinal)) return null;
        var hash = Secrets.Sha256Hex(s);
        return await db.DecisionGrants.AsNoTracking().FirstOrDefaultAsync(x => x.SecretHash == hash && x.TenantId == tenantId && x.DeviceCredentialId == deviceCredentialId, ct);
    }

    private Task<Credential?> ActiveDeviceCredentialAsync(Guid tenantId, Guid credentialId, DateTimeOffset now, CancellationToken ct)
        => db.Credentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.TenantId == tenantId && c.Kind == CredentialKinds.Device && c.RevokedAt == null && (c.ExpiresAt == null || c.ExpiresAt > now), ct);
}
