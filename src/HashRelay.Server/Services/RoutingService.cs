using System.Text.Json;
using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using HashRelay.Server.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Services;

public sealed class HashRelayOptions
{
    public string PublicUrl { get; set; } = "";
    public string DatabasePath { get; set; } = "";
    public string ImagesPath { get; set; } = "";
    public bool AllowSignup { get; set; } = true;
    public int ReplayWindowHours { get; set; } = 24;
    public int PublishRateLimitPerMinute { get; set; } = 120;
}

/// <summary>
/// Authorises publications, matches the union of subscribers for the addressed hashtags, records one delivery per
/// endpoint (OncePerEndpoint) and dispatches over live hub connections. Offline endpoints keep a queued delivery that
/// is replayed on their next Hello, within the replay window.
/// </summary>
public sealed class RoutingService(RelayDbContext db, TimeProvider clock, ConnectionRegistry registry, IHubContext<NotificationsHub> hub,
    IOptions<HashRelayOptions> options, IDiagramRenderer diagrams, ImageStore images, PublicBase publicBase, ILogger<RoutingService> log)
{
    public async Task<PublishResult> PublishAsync(Guid tenantId, Guid credentialId, PublishRequest req, CancellationToken ct)
    {
        var v = Validate(req);
        var payloadHash = Secrets.Sha256Hex(JsonSerializer.Serialize(new { v.Tags, v.Title, v.Body, v.Url, v.Priority, v.Markdown, v.ImageUrl, v.Diagram }));
        var now = clock.GetUtcNow();
        var id = req.NotificationId ?? Guid.NewGuid();
        if (id == Guid.Empty) throw ApiException.Validation("notificationId must be a non-empty UUID.", "notificationId");

        if (req.NotificationId is not null)
        {
            var existing = await db.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
            if (existing is not null) return await DuplicateAsync(existing, tenantId, payloadHash, ct);
        }

        // Render the diagram before anything is persisted so the first delivery already carries the image.
        string? diagramImageUrl = null;
        if (v.Diagram is not null && diagrams.Enabled)
        {
            var cached = ImageStore.CachedName("mermaid:" + v.Diagram, "png");
            if (images.Exists(cached)) diagramImageUrl = publicBase.Absolute(ImageStore.PublicPath(cached));
            else
            {
                var png = await diagrams.RenderPngAsync(v.Diagram, ct);
                if (png is not null && png.Length <= Limits.ImageMaxBytes)
                    diagramImageUrl = publicBase.Absolute(ImageStore.PublicPath(await images.SaveCachedAsync("mermaid:" + v.Diagram, png, "png", ct)));
            }
        }

        var endpointRefs = await db.Subscriptions.Where(s => s.TenantId == tenantId && v.Tags.Contains(s.Tag))
            .Select(s => s.EndpointRef).Distinct().ToListAsync(ct);

        var notification = new Notification
        {
            Id = id, TenantId = tenantId, TagsCsv = string.Join(',', v.Tags), Title = v.Title, Body = v.Body, Url = v.Url,
            Priority = v.Priority, Markdown = v.Markdown, ImageUrl = v.ImageUrl, Diagram = v.Diagram, DiagramImageUrl = diagramImageUrl,
            PayloadHash = payloadHash, PublishedByCredentialId = credentialId, PublishedAt = now,
            MatchedEndpoints = endpointRefs.Count,
        };
        var deliveries = endpointRefs.Select(ep => new Delivery
        {
            Id = Guid.NewGuid(), TenantId = tenantId, NotificationId = id, EndpointRef = ep, Status = DeliveryStatus.Queued, CreatedAt = now,
        }).ToList();
        db.Notifications.Add(notification);
        db.Deliveries.AddRange(deliveries);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (req.NotificationId is not null)
        {
            // Lost a race with an identical notificationId: fall back to the idempotency path.
            db.ChangeTracker.Clear();
            var existing = await db.Notifications.AsNoTracking().FirstAsync(n => n.Id == id, ct);
            return await DuplicateAsync(existing, tenantId, payloadHash, ct);
        }

        var tenantName = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).FirstAsync(ct);
        var live = 0;
        foreach (var d in deliveries)
        {
            if (!registry.IsOnline(d.EndpointRef)) continue;
            var msg = ToMessage(notification, d, tenantName, replayed: false);
            try
            {
                await hub.Clients.Group(EndpointService.GroupFor(d.EndpointRef)).SendAsync(HubMethods.NotificationReceived, msg, ct);
                d.Status = DeliveryStatus.Sent;
                d.SentAt = now;
                d.Attempts = 1;
                live++;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Live dispatch to endpoint {EndpointRef} failed; left queued", d.EndpointRef);
            }
        }
        if (live > 0) await db.SaveChangesAsync(ct);

        log.LogInformation("Published {NotificationId} to {Tags} in tenant {TenantId}: {Matched} matched, {Live} live",
            id, Tags.DisplayAll(v.Tags), tenantId, endpointRefs.Count, live);
        return new PublishResult(id, now, v.Tags, v.Title, endpointRefs.Count, live, endpointRefs.Count - live, Duplicate: false,
            DetailUrl(notification), ToastImage(notification));
    }

    private async Task<PublishResult> DuplicateAsync(Notification existing, Guid tenantId, string payloadHash, CancellationToken ct)
    {
        // A notificationId is globally unique; a collision from another tenant or with a different payload is a conflict.
        if (existing.TenantId != tenantId || existing.PayloadHash != payloadHash)
            throw ApiException.Conflict("A notification with this notificationId already exists with a different payload.");
        var delivered = await db.Deliveries.CountAsync(d => d.NotificationId == existing.Id && d.Status != DeliveryStatus.Queued, ct);
        return new PublishResult(existing.Id, existing.PublishedAt, existing.Tags, existing.Title, existing.MatchedEndpoints,
            delivered, existing.MatchedEndpoints - delivered, Duplicate: true, DetailUrl(existing), ToastImage(existing));
    }

    private sealed record Validated(IReadOnlyList<string> Tags, string Title, string? Body, string? Url, string Priority, string? Markdown, string? ImageUrl, string? Diagram);

    private static Validated Validate(PublishRequest req)
    {
        var details = new Dictionary<string, string[]>();
        IReadOnlyList<string> tags = [];
        if (req.Tags is null || req.Tags.Count == 0) details["tags"] = ["Address at least one hashtag."];
        else
        {
            try
            {
                tags = Tags.NormalizeAll(req.Tags);
                if (tags.Count > Tags.MaxPerPublication) details["tags"] = [$"At most {Tags.MaxPerPublication} distinct hashtags per notification."];
            }
            catch (ArgumentException ex) { details["tags"] = [ex.Message]; }
        }

        var title = (req.Title ?? "").Trim();
        if (title.Length is < 1 or > Limits.TitleMax) details["title"] = [$"Title must be 1–{Limits.TitleMax} characters."];

        var markdown = string.IsNullOrWhiteSpace(req.Markdown) ? null : req.Markdown.Replace("\r\n", "\n").Trim();
        if (markdown is not null && markdown.Length > Limits.MarkdownMax) details["markdown"] = [$"markdown must be at most {Limits.MarkdownMax} characters."];

        var body = string.IsNullOrWhiteSpace(req.Body) ? null : req.Body.Trim();
        body ??= MarkdownRenderer.Summary(markdown, 200);
        if (body is not null && body.Length > Limits.BodyMax) details["body"] = [$"Body must be at most {Limits.BodyMax} characters."];

        var url = string.IsNullOrWhiteSpace(req.Url) ? null : req.Url.Trim();
        if (url is not null && !IsHttpUrl(url)) details["url"] = ["url must be an absolute http(s) URL."];

        var imageUrl = string.IsNullOrWhiteSpace(req.ImageUrl) ? null : req.ImageUrl.Trim();
        if (imageUrl is not null && !IsHttpUrl(imageUrl)) details["imageUrl"] = ["imageUrl must be an absolute http(s) URL (upload a file with POST /api/v1/images)."];

        var diagram = string.IsNullOrWhiteSpace(req.Diagram) ? null : req.Diagram.Replace("\r\n", "\n").Trim();
        diagram ??= MarkdownRenderer.FirstMermaidBlock(markdown);
        if (diagram is not null && diagram.Length > Limits.DiagramMax) details["diagram"] = [$"diagram must be at most {Limits.DiagramMax} characters."];

        if (!Priorities.TryNormalize(req.Priority, out var priority)) details["priority"] = [$"priority must be one of: {string.Join(", ", Priorities.All)}."];

        if (details.Count > 0) throw new ApiException(400, ErrorCodes.Validation, "The publication is not valid.", details);
        return new Validated(tags, title, body, url, priority, markdown, imageUrl, diagram);
    }

    private static bool IsHttpUrl(string url)
        => url.Length <= Limits.UrlMax && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    /// <summary>Replays queued deliveries to a freshly connected endpoint; expires those outside the replay window.</summary>
    public async Task<int> ReplayAsync(Guid tenantId, Guid endpointRef, string connectionId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cutoff = now.AddHours(-Math.Max(0, options.Value.ReplayWindowHours));
        var queued = await db.Deliveries.Include(d => d.Notification)
            .Where(d => d.EndpointRef == endpointRef && d.TenantId == tenantId && d.Status == DeliveryStatus.Queued)
            .OrderBy(d => d.CreatedAt).Take(200).ToListAsync(ct);
        if (queued.Count == 0) return 0;

        var tenantName = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).FirstAsync(ct);
        var replayed = 0;
        foreach (var d in queued)
        {
            if (d.CreatedAt < cutoff) { d.Status = DeliveryStatus.Expired; continue; }
            await hub.Clients.Client(connectionId).SendAsync(HubMethods.NotificationReceived, ToMessage(d.Notification, d, tenantName, replayed: true), ct);
            d.Status = DeliveryStatus.Sent;
            d.SentAt = now;
            d.Attempts++;
            replayed++;
        }
        await db.SaveChangesAsync(ct);
        return replayed;
    }

    public async Task AckAsync(Guid tenantId, Guid endpointRef, Guid deliveryId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Deliveries.Where(d => d.Id == deliveryId && d.TenantId == tenantId && d.EndpointRef == endpointRef && d.Status != DeliveryStatus.Acked)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatus.Acked).SetProperty(d => d.AckedAt, now), ct);
    }

    public static bool IsRich(Notification n) => n.Markdown is not null || n.Diagram is not null;
    public static string? ToastImage(Notification n) => n.ImageUrl ?? n.DiagramImageUrl;
    public string DetailUrl(Notification n) => publicBase.Absolute($"/n/{n.Id}");

    private NotificationMessage ToMessage(Notification n, Delivery d, string tenantName, bool replayed)
        => new(n.Id, n.TenantId, n.Tags, n.Title, n.Body, n.PublishedAt, d.Id, n.Url, n.Priority, tenantName, replayed,
            ToastImage(n), DetailUrl(n), IsRich(n));

    public async Task<List<NotificationSummary>> RecentAsync(Guid tenantId, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 200);
        var rows = await db.Notifications.AsNoTracking().Where(n => n.TenantId == tenantId).OrderByDescending(n => n.PublishedAt).Take(limit)
            .Select(n => new
            {
                n.Id, n.TagsCsv, n.Title, n.Body, n.Url, n.Priority, n.PublishedAt, n.MatchedEndpoints,
                Delivered = n.Deliveries.Count(d => d.Status != DeliveryStatus.Queued),
                HasMarkdown = n.Markdown != null, HasDiagram = n.Diagram != null, n.ImageUrl, n.DiagramImageUrl,
            })
            .ToListAsync(ct);
        var baseUrl = publicBase.Url;
        return rows.Select(n => new NotificationSummary(n.Id, n.TagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries), n.Title, n.Body, n.Url, n.Priority,
            n.PublishedAt, n.MatchedEndpoints, n.Delivered, n.HasMarkdown, n.HasDiagram, n.ImageUrl ?? n.DiagramImageUrl, $"{baseUrl}/n/{n.Id}")).ToList();
    }

    /// <summary>A notification for the detail page: only members of its tenant may see it.</summary>
    public Task<Notification?> FindAsync(Guid id, IEnumerable<Guid> memberTenantIds, CancellationToken ct)
    {
        var tenants = memberTenantIds.ToList();
        return db.Notifications.AsNoTracking().Include(n => n.Deliveries)
            .FirstOrDefaultAsync(n => n.Id == id && tenants.Contains(n.TenantId), ct);
    }
}
