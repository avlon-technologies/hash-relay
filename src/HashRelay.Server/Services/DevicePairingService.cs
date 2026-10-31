using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Services;

/// <summary>
/// Device pairing in the shape of the OAuth 2.0 device-authorization grant: the agent or CLI asks for a code,
/// the person approves it in the dashboard for one workspace, and the device polls until a subscriber token
/// bound to its endpoint is issued. Installed clients therefore never hold a publisher API key by default.
/// </summary>
public sealed class DevicePairingService(RelayDbContext db, TimeProvider clock, CredentialService credentials, EndpointService endpoints, IOptions<HashRelayOptions> options)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public const int PollIntervalSeconds = 3;

    public async Task<DeviceCodeResponse> BeginAsync(DeviceCodeRequest req, string requestBaseUrl, CancellationToken ct)
    {
        var endpointId = EndpointService.ValidateEndpointId(req.EndpointId);
        var client = (req.Client ?? "").Trim().ToLowerInvariant();
        if (client is not ("agent" or "cli")) throw ApiException.Validation("client must be 'agent' or 'cli'.", "client");
        var name = (req.DeviceName ?? "").Trim();
        if (name.Length is < 1 or > Limits.DeviceNameMax) throw ApiException.Validation($"deviceName must be 1–{Limits.DeviceNameMax} characters.", "deviceName");

        var now = clock.GetUtcNow();
        // Housekeeping: drop expired requests so user codes can be reused.
        await db.DevicePairings.Where(p => p.ExpiresAt < now.AddHours(-1)).ExecuteDeleteAsync(ct);

        var deviceCode = Secrets.NewDeviceCode();
        string userCode;
        do { userCode = Secrets.NewUserCode(); }
        while (await db.DevicePairings.AnyAsync(p => p.UserCode == userCode && p.ExpiresAt > now, ct));

        db.DevicePairings.Add(new DevicePairing
        {
            Id = Guid.NewGuid(), DeviceCodeHash = Secrets.Sha256Hex(deviceCode), UserCode = userCode, EndpointId = endpointId,
            DeviceName = name, Platform = (req.Platform ?? "unknown").Trim().ToLowerInvariant(), AgentVersion = req.AgentVersion,
            Client = client, Status = PairingStatus.Pending, CreatedAt = now, ExpiresAt = now + Lifetime,
        });
        await db.SaveChangesAsync(ct);

        var baseUrl = string.IsNullOrWhiteSpace(options.Value.PublicUrl) ? requestBaseUrl : options.Value.PublicUrl.TrimEnd('/');
        return new DeviceCodeResponse(deviceCode, userCode, $"{baseUrl}/pair", $"{baseUrl}/pair?code={userCode}", (int)Lifetime.TotalSeconds, PollIntervalSeconds);
    }

    public async Task<DeviceTokenResponse> PollAsync(string deviceCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceCode)) throw ApiException.Validation("deviceCode is required.", "deviceCode");
        var hash = Secrets.Sha256Hex(deviceCode.Trim());
        var now = clock.GetUtcNow();
        var pairing = await db.DevicePairings.FirstOrDefaultAsync(p => p.DeviceCodeHash == hash, ct);
        if (pairing is null) return new DeviceTokenResponse("expired", null, null, null);
        pairing.LastPolledAt = now;

        if (pairing.Status == PairingStatus.Pending && pairing.ExpiresAt <= now)
        {
            await db.SaveChangesAsync(ct);
            return new DeviceTokenResponse("expired", null, null, null);
        }
        if (pairing.Status == PairingStatus.Denied) { await db.SaveChangesAsync(ct); return new DeviceTokenResponse("denied", null, null, null); }
        if (pairing.Status == PairingStatus.Pending) { await db.SaveChangesAsync(ct); return new DeviceTokenResponse("pending", null, null, null); }
        if (pairing.Status == PairingStatus.Consumed) return new DeviceTokenResponse("expired", null, null, null);

        // Approved: hand over the token exactly once.
        var token = pairing.IssuedToken;
        pairing.IssuedToken = null;
        pairing.Status = PairingStatus.Consumed;
        await db.SaveChangesAsync(ct);
        var cred = await db.Credentials.AsNoTracking().FirstAsync(c => c.Id == pairing.CredentialId, ct);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == pairing.TenantId, ct);
        return new DeviceTokenResponse("approved", token, new TenantInfo(tenant.Id, tenant.Slug, tenant.Name), CredentialService.ToInfo(cred));
    }

    public static string NormalizeUserCode(string? code)
    {
        var raw = new string((code ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return raw.Length == 8 ? raw[..4] + "-" + raw[4..] : raw;
    }

    public async Task<DevicePairing?> FindPendingAsync(string? userCode, CancellationToken ct)
    {
        var code = NormalizeUserCode(userCode);
        if (code.Length != 9) return null;
        var now = clock.GetUtcNow();
        return await db.DevicePairings.FirstOrDefaultAsync(p => p.UserCode == code && p.Status == PairingStatus.Pending && p.ExpiresAt > now, ct);
    }

    public async Task<Credential> ApproveAsync(Guid pairingId, Guid accountId, Guid tenantId, IEnumerable<string> scopes, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var pairing = await db.DevicePairings.FirstOrDefaultAsync(p => p.Id == pairingId && p.Status == PairingStatus.Pending && p.ExpiresAt > now, ct)
                      ?? throw ApiException.NotFound("This pairing request has expired or was already handled.");
        var ep = await endpoints.RegisterAsync(tenantId, pairing.EndpointId, pairing.DeviceName, pairing.Platform, pairing.AgentVersion, null, connected: false, ct);
        var label = pairing.Client == "cli" ? $"{pairing.DeviceName} (CLI)" : pairing.DeviceName;
        var (cred, secret) = await credentials.IssueDeviceTokenAsync(tenantId, accountId, ep.Id, label, scopes, ct);
        pairing.Status = PairingStatus.Approved;
        pairing.TenantId = tenantId;
        pairing.ApprovedByAccountId = accountId;
        pairing.CredentialId = cred.Id;
        pairing.IssuedToken = secret;
        await db.SaveChangesAsync(ct);
        return cred;
    }

    public async Task DenyAsync(Guid pairingId, CancellationToken ct)
    {
        await db.DevicePairings.Where(p => p.Id == pairingId && p.Status == PairingStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PairingStatus.Denied), ct);
    }
}
