using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using HashRelay.Server.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Services;

/// <summary>Endpoint registration and the persisted hashtag subscriptions that drive routing.</summary>
public sealed class EndpointService(RelayDbContext db, TimeProvider clock, ConnectionRegistry registry, IHubContext<NotificationsHub> hub)
{
    public static string GroupFor(Guid endpointRef) => $"ep:{endpointRef:n}";

    public static string ValidateEndpointId(string? endpointId)
    {
        endpointId = (endpointId ?? "").Trim();
        if (endpointId.Length is < 1 or > Limits.EndpointIdMax || endpointId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')))
            throw ApiException.Validation($"endpointId must be 1–{Limits.EndpointIdMax} characters of letters, digits, '-', '_', '.' or ':'.", "endpointId");
        return endpointId;
    }

    /// <summary>
    /// Upserts an endpoint. A device-bound credential may only act as its own endpoint; a tenant API key may act as any.
    /// A push token is stored when <paramref name="pushProvider"/> is given (an empty token clears it); null leaves it unchanged.
    /// </summary>
    public async Task<Data.Endpoint> RegisterAsync(Guid tenantId, string endpointId, string? name, string? platform, string? agentVersion,
        string? pushProvider, string? pushToken, Guid? boundEndpointRef, bool connected, CancellationToken ct)
    {
        endpointId = ValidateEndpointId(endpointId);
        var (provider, token) = ValidatePush(pushProvider, pushToken);
        var now = clock.GetUtcNow();
        var ep = await db.Endpoints.Include(e => e.Subscriptions).FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EndpointId == endpointId, ct);

        if (boundEndpointRef is not null && ep?.Id != boundEndpointRef)
            throw ApiException.Forbidden("This device token is bound to a different endpoint.");

        if (ep is null)
        {
            ep = new Data.Endpoint
            {
                Id = Guid.NewGuid(), TenantId = tenantId, EndpointId = endpointId,
                Name = Clean(name, Limits.DeviceNameMax) ?? endpointId,
                Platform = Clean(platform, 32)?.ToLowerInvariant() ?? "unknown",
                AgentVersion = Clean(agentVersion, 32), CreatedAt = now, LastSeenAt = now,
            };
            db.Endpoints.Add(ep);
        }
        else
        {
            if (Clean(name, Limits.DeviceNameMax) is { } n) ep.Name = n;
            if (Clean(platform, 32) is { } p) ep.Platform = p.ToLowerInvariant();
            if (Clean(agentVersion, 32) is { } v) ep.AgentVersion = v;
            ep.LastSeenAt = now;
        }
        if (connected) ep.LastConnectedAt = now;
        if (provider is not null)
        {
            if (token is null)
            {
                ep.PushProvider = null; ep.PushToken = null; ep.PushTokenUpdatedAt = now;
            }
            else if (ep.PushToken != token || ep.PushProvider != provider)
            {
                ep.PushProvider = provider; ep.PushToken = token; ep.PushTokenUpdatedAt = now; ep.PushInvalidatedAt = null;
            }
        }
        await db.SaveChangesAsync(ct);
        if (token is not null)
        {
            // A reinstalled app may pair under a new endpoint id with the same device token: keep one owner per token so the device is pushed once.
            await db.Endpoints.Where(e => e.PushToken == token && e.Id != ep.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.PushToken, (string?)null).SetProperty(e => e.PushTokenUpdatedAt, now), ct);
        }
        return ep;
    }

    /// <summary>Records that the provider no longer knows the endpoint's token; pushes stop until the app registers a new one.</summary>
    public async Task InvalidatePushTokenAsync(Guid endpointRef, string token, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Endpoints.Where(e => e.Id == endpointRef && e.PushToken == token)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PushToken, (string?)null).SetProperty(e => e.PushInvalidatedAt, now), ct);
    }

    private static (string? Provider, string? Token) ValidatePush(string? pushProvider, string? pushToken)
    {
        var provider = pushProvider?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(provider)) return (null, null);
        if (!PushProviders.All.Contains(provider))
            throw ApiException.Validation($"pushProvider must be one of: {string.Join(", ", PushProviders.All)}.", "pushProvider");
        var token = pushToken?.Trim();
        if (string.IsNullOrEmpty(token)) return (provider, null);
        if (token.Length > PushProviders.TokenMax || token.Any(char.IsWhiteSpace))
            throw ApiException.Validation($"pushToken must be at most {PushProviders.TokenMax} characters without whitespace.", "pushToken");
        return (provider, token);
    }

    private static string? Clean(string? s, int max)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length > max ? s[..max] : s;
    }

    public async Task<Data.Endpoint> RequireAsync(Guid tenantId, string endpointId, Guid? boundEndpointRef, CancellationToken ct)
    {
        endpointId = ValidateEndpointId(endpointId);
        var ep = await db.Endpoints.Include(e => e.Subscriptions).FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EndpointId == endpointId, ct);
        if (boundEndpointRef is not null && ep?.Id != boundEndpointRef) throw ApiException.Forbidden("This device token is bound to a different endpoint.");
        return ep ?? throw ApiException.NotFound($"Endpoint '{endpointId}' is not registered in this workspace.");
    }

    /// <summary>Resolves an endpoint for subscription management, creating a placeholder registration when a tenant key names a new endpoint.</summary>
    public async Task<Data.Endpoint> ResolveForSubscriptionsAsync(Guid tenantId, string endpointId, Guid? boundEndpointRef, CancellationToken ct)
    {
        endpointId = ValidateEndpointId(endpointId);
        var ep = await db.Endpoints.Include(e => e.Subscriptions).FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EndpointId == endpointId, ct);
        if (boundEndpointRef is not null && ep?.Id != boundEndpointRef) throw ApiException.Forbidden("This device token is bound to a different endpoint.");
        return ep ?? await RegisterAsync(tenantId, endpointId, null, null, null, null, null, null, connected: false, ct);
    }

    public async Task<IReadOnlyList<string>> AddSubscriptionsAsync(Data.Endpoint ep, IEnumerable<string> rawTags, CancellationToken ct)
    {
        var tags = NormalizeTags(rawTags);
        var now = clock.GetUtcNow();
        var existing = ep.Subscriptions.Select(s => s.Tag).ToHashSet();
        foreach (var t in tags.Where(t => !existing.Contains(t)))
            AddSubscription(ep, t, now);
        return await CommitAsync(ep, ct);
    }

    public async Task<IReadOnlyList<string>> RemoveSubscriptionsAsync(Data.Endpoint ep, IEnumerable<string> rawTags, CancellationToken ct)
    {
        var tags = NormalizeTags(rawTags).ToHashSet();
        foreach (var s in ep.Subscriptions.Where(s => tags.Contains(s.Tag)).ToList())
        {
            ep.Subscriptions.Remove(s);
            db.Subscriptions.Remove(s);
        }
        return await CommitAsync(ep, ct);
    }

    public async Task<IReadOnlyList<string>> ReplaceSubscriptionsAsync(Data.Endpoint ep, IEnumerable<string> rawTags, CancellationToken ct)
    {
        var tags = NormalizeTags(rawTags).ToHashSet();
        var now = clock.GetUtcNow();
        foreach (var s in ep.Subscriptions.Where(s => !tags.Contains(s.Tag)).ToList())
        {
            ep.Subscriptions.Remove(s);
            db.Subscriptions.Remove(s);
        }
        var have = ep.Subscriptions.Select(s => s.Tag).ToHashSet();
        foreach (var t in tags.Where(t => !have.Contains(t)))
            AddSubscription(ep, t, now);
        return await CommitAsync(ep, ct);
    }

    /// <summary>New rows must go through the DbSet: entities discovered via navigation with a pre-set key are treated as existing (UPDATE).</summary>
    private void AddSubscription(Data.Endpoint ep, string tag, DateTimeOffset now)
    {
        var sub = new Subscription { Id = Guid.NewGuid(), TenantId = ep.TenantId, EndpointRef = ep.Id, Tag = tag, CreatedAt = now };
        db.Subscriptions.Add(sub); // relationship fix-up also adds it to ep.Subscriptions
    }

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> raw)
    {
        try { return Tags.NormalizeAll(raw); }
        catch (ArgumentException ex) { throw ApiException.Validation(ex.Message, "tags"); }
    }

    private async Task<IReadOnlyList<string>> CommitAsync(Data.Endpoint ep, CancellationToken ct)
    {
        ep.LastSeenAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var tags = CurrentTags(ep);
        if (registry.IsOnline(ep.Id))
            await hub.Clients.Group(GroupFor(ep.Id)).SendAsync(HubMethods.SubscriptionsChanged, tags, ct);
        return tags;
    }

    public static IReadOnlyList<string> CurrentTags(Data.Endpoint ep) => ep.Subscriptions.Select(s => s.Tag).Order(StringComparer.Ordinal).ToList();

    public Task<Data.Endpoint?> GetByRefAsync(Guid tenantId, Guid endpointRef, CancellationToken ct)
        => db.Endpoints.Include(e => e.Subscriptions).FirstOrDefaultAsync(e => e.Id == endpointRef && e.TenantId == tenantId, ct);

    public async Task<List<Data.Endpoint>> ListAsync(Guid tenantId, CancellationToken ct)
        => await db.Endpoints.AsNoTracking().Include(e => e.Subscriptions).Where(e => e.TenantId == tenantId).OrderByDescending(e => e.LastSeenAt).ToListAsync(ct);

    public async Task DeleteAsync(Guid tenantId, Guid endpointRef, CancellationToken ct)
    {
        var ep = await db.Endpoints.FirstOrDefaultAsync(e => e.Id == endpointRef && e.TenantId == tenantId, ct) ?? throw ApiException.NotFound("Endpoint not found.");
        var now = clock.GetUtcNow();
        var creds = await db.Credentials.Where(c => c.TenantId == tenantId && c.EndpointRef == endpointRef && c.RevokedAt == null).ToListAsync(ct);
        foreach (var c in creds) c.RevokedAt = now;
        db.Endpoints.Remove(ep);
        await db.SaveChangesAsync(ct);
        foreach (var c in creds)
        {
            var conns = registry.ConnectionsForCredential(c.Id);
            if (conns.Count > 0) await hub.Clients.Clients(conns).SendAsync(HubMethods.CredentialRevoked, "endpoint removed", ct);
        }
    }

    public async Task MarkDisconnectedAsync(Guid endpointRef, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Endpoints.Where(e => e.Id == endpointRef)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LastDisconnectedAt, now).SetProperty(e => e.LastSeenAt, now), ct);
    }

    public EndpointInfo ToInfo(Data.Endpoint ep)
        => new(ep.EndpointId, ep.Name, ep.Platform, registry.IsOnline(ep.Id), ep.LastSeenAt, CurrentTags(ep), ep.CanPush ? ep.PushProvider : null);
}
