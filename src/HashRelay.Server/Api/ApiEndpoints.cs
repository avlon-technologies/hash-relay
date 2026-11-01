using System.Reflection;
using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HashRelay.Server.Api;

/// <summary>HTTP API v1 for publishers (API keys) and subscribers (device tokens or API keys with the subscribe scope).</summary>
public static class ApiEndpoints
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static IEndpointRouteBuilder MapHashRelayApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", Health).AllowAnonymous();
        app.MapGet("/health/live", Health).AllowAnonymous();

        var api = app.MapGroup("/api/v1").AddEndpointFilter<ApiExceptionFilter>();

        // ---- device pairing (anonymous: the device has no credential yet) ----
        api.MapPost("/device/code", async (DeviceCodeRequest req, HttpContext ctx, DevicePairingService pairing, CancellationToken ct) =>
            Results.Ok(await pairing.BeginAsync(req, $"{ctx.Request.Scheme}://{ctx.Request.Host}", ct)))
            .RequireRateLimiting("pairing");
        api.MapPost("/device/token", async (DeviceTokenRequest req, DevicePairingService pairing, CancellationToken ct) =>
            Results.Ok(await pairing.PollAsync(req.DeviceCode, ct)))
            .RequireRateLimiting("pairing");

        // ---- identity ----
        api.MapGet("/me", async (HttpContext ctx, Services.CredentialService creds, Data.RelayDbContext db, CancellationToken ct) =>
        {
            var user = ctx.User;
            var cred = await db.Credentials.FindAsync([user.CredentialId()], ct);
            return Results.Ok(new MeResponse(new TenantInfo(user.TenantId(), user.TenantSlug(), user.TenantName()),
                CredentialService.ToInfo(cred!), DateTimeOffset.UtcNow, Version));
        }).RequireAuthorization(ApiAuth.PolicyAny);

        // ---- publish ----
        api.MapPost("/notifications", async (PublishRequest req, HttpContext ctx, RoutingService routing, CancellationToken ct) =>
        {
            var result = await routing.PublishAsync(ctx.User.TenantId(), ctx.User.CredentialId(), req, ct, ServicePolicy.Of(ctx.User),
                PublisherIdentity.Of(ctx.User), PublishChannel.Of(ctx));
            return Results.Json(result, statusCode: result.Duplicate ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
        }).RequireAuthorization(ApiAuth.PolicyPublish).RequireRateLimiting("publish");

        api.MapGet("/notifications", async ([FromQuery] int? limit, HttpContext ctx, RoutingService routing, CancellationToken ct) =>
            Results.Ok(await routing.RecentAsync(ctx.User.TenantId(), limit ?? 20, ct)))
            .RequireAuthorization(ApiAuth.PolicyRead);

        // ---- images: raw body upload (image/png|jpeg|gif|webp, ≤ 3 MB) → public unguessable URL for PublishRequest.imageUrl ----
        api.MapPost("/images", async (HttpContext ctx, ImageStore images, PublicBase publicBase, CancellationToken ct) =>
        {
            var ext = ImageStore.ExtensionFor(ctx.Request.ContentType);
            if (ext is null) throw ApiException.Validation($"Content-Type must be one of: {string.Join(", ", Limits.ImageContentTypes)}.", "contentType");
            if (ctx.Request.ContentLength is > Limits.ImageMaxBytes) throw ApiException.Validation($"Image must be at most {Limits.ImageMaxBytes / 1024 / 1024} MB.", "body");
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (ms.Length + read > Limits.ImageMaxBytes) throw ApiException.Validation($"Image must be at most {Limits.ImageMaxBytes / 1024 / 1024} MB.", "body");
                ms.Write(buffer, 0, read);
            }
            if (ms.Length == 0 || !ImageStore.LooksLike(ms.GetBuffer().AsSpan(0, (int)ms.Length), ext))
                throw ApiException.Validation("The body is not a valid image of the declared type.", "body");
            var name = await images.SaveAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), ext, ct);
            var url = publicBase.Absolute(ImageStore.PublicPath(name));
            return Results.Created(url, new ImageUploadResult(name, url, ms.Length, ImageStore.ContentTypeFor(name)));
        }).RequireAuthorization(ApiAuth.PolicyImages).RequireRateLimiting("publish").DisableAntiforgery();

        app.MapGet(ImageStore.RoutePrefix + "{name}", (string name, ImageStore images) =>
        {
            var path = images.PathFor(name);
            if (path is null) return Results.NotFound();
            return Results.File(path, ImageStore.ContentTypeFor(name), lastModified: File.GetLastWriteTimeUtc(path));
        }).AllowAnonymous();

        // ---- endpoints ----
        api.MapGet("/endpoints", async (HttpContext ctx, EndpointService endpoints, CancellationToken ct) =>
        {
            var list = await endpoints.ListAsync(ctx.User.TenantId(), ct);
            var bound = ctx.User.BoundEndpointRef();
            if (bound is not null) list = list.Where(e => e.Id == bound).ToList();
            return Results.Ok(list.Select(endpoints.ToInfo).ToList());
        }).RequireAuthorization(ApiAuth.PolicyEndpoints);

        api.MapPost("/endpoints", async (RegisterEndpointRequest req, HttpContext ctx, EndpointService endpoints, CancellationToken ct) =>
        {
            var tenantId = ctx.User.TenantId();
            var endpointId = EndpointService.ValidateEndpointId(req.EndpointId);
            var existed = (await endpoints.ListAsync(tenantId, ct)).Any(e => e.EndpointId == endpointId);
            var ep = await endpoints.RegisterAsync(tenantId, endpointId, req.Name, req.Platform, req.AgentVersion, req.PushProvider, req.PushToken, ctx.User.BoundEndpointRef(), connected: false, ct);
            return Results.Json(endpoints.ToInfo(ep), statusCode: existed ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        }).RequireAuthorization(ApiAuth.PolicySubscribe);

        api.MapPut("/endpoints/{endpointId}", async (string endpointId, RegisterEndpointRequest req, HttpContext ctx, EndpointService endpoints, CancellationToken ct) =>
        {
            var ep = await endpoints.RegisterAsync(ctx.User.TenantId(), endpointId, req.Name, req.Platform, req.AgentVersion, req.PushProvider, req.PushToken, ctx.User.BoundEndpointRef(), connected: false, ct);
            return Results.Ok(endpoints.ToInfo(ep));
        }).RequireAuthorization(ApiAuth.PolicySubscribe);

        // ---- deliveries: an endpoint's inbox, and acknowledgement over HTTP for clients that received a push while not connected ----
        api.MapGet("/endpoints/{endpointId}/deliveries", async (string endpointId, [FromQuery] int? limit, HttpContext ctx, EndpointService endpoints, RoutingService routing, CancellationToken ct) =>
        {
            var ep = await endpoints.RequireAsync(ctx.User.TenantId(), endpointId, ctx.User.BoundEndpointRef(), ct);
            return Results.Ok(await routing.ListDeliveriesAsync(ctx.User.TenantId(), ep.Id, limit ?? 50, ct));
        }).RequireAuthorization(ApiAuth.PolicyEndpoints);

        api.MapPost("/endpoints/{endpointId}/deliveries/{deliveryId:guid}/ack", async (string endpointId, Guid deliveryId, HttpContext ctx, EndpointService endpoints, RoutingService routing, CancellationToken ct) =>
        {
            var ep = await endpoints.RequireAsync(ctx.User.TenantId(), endpointId, ctx.User.BoundEndpointRef(), ct);
            await routing.AckAsync(ctx.User.TenantId(), ep.Id, deliveryId, ct);
            return Results.NoContent();
        }).RequireAuthorization(ApiAuth.PolicySubscribe);

        api.MapGet("/endpoints/{endpointId}", async (string endpointId, HttpContext ctx, EndpointService endpoints, CancellationToken ct) =>
            Results.Ok(endpoints.ToInfo(await endpoints.RequireAsync(ctx.User.TenantId(), endpointId, ctx.User.BoundEndpointRef(), ct))))
            .RequireAuthorization(ApiAuth.PolicyEndpoints);

        // ---- subscriptions (bulk) ----
        api.MapGet("/endpoints/{endpointId}/subscriptions", async (string endpointId, HttpContext ctx, EndpointService endpoints, CancellationToken ct) =>
        {
            var ep = await endpoints.RequireAsync(ctx.User.TenantId(), endpointId, ctx.User.BoundEndpointRef(), ct);
            return Results.Ok(new SubscriptionsResponse(ep.EndpointId, EndpointService.CurrentTags(ep)));
        }).RequireAuthorization(ApiAuth.PolicyEndpoints);

        api.MapPut("/endpoints/{endpointId}/subscriptions", async (string endpointId, SubscriptionsRequest req, HttpContext ctx, EndpointService endpoints, CancellationToken ct) =>
        {
            var ep = await endpoints.ResolveForSubscriptionsAsync(ctx.User.TenantId(), endpointId, ctx.User.BoundEndpointRef(), ct);
            return Results.Ok(new SubscriptionsResponse(ep.EndpointId, await endpoints.ReplaceSubscriptionsAsync(ep, req.Tags ?? [], ct)));
        }).RequireAuthorization(ApiAuth.PolicySubscribe);

        api.MapPost("/endpoints/{endpointId}/subscriptions", async (string endpointId, SubscriptionsRequest req, HttpContext ctx, EndpointService endpoints, CancellationToken ct) =>
        {
            var ep = await endpoints.ResolveForSubscriptionsAsync(ctx.User.TenantId(), endpointId, ctx.User.BoundEndpointRef(), ct);
            return Results.Ok(new SubscriptionsResponse(ep.EndpointId, await endpoints.AddSubscriptionsAsync(ep, req.Tags ?? [], ct)));
        }).RequireAuthorization(ApiAuth.PolicySubscribe);

        api.MapDelete("/endpoints/{endpointId}/subscriptions", async (string endpointId, [FromQuery] string[] tags, HttpContext ctx, EndpointService endpoints, CancellationToken ct) =>
        {
            var ep = await endpoints.ResolveForSubscriptionsAsync(ctx.User.TenantId(), endpointId, ctx.User.BoundEndpointRef(), ct);
            return Results.Ok(new SubscriptionsResponse(ep.EndpointId, await endpoints.RemoveSubscriptionsAsync(ep, tags, ct)));
        }).RequireAuthorization(ApiAuth.PolicySubscribe);

        // ---- subscriptions (single tag, idempotent 204, per the M1 contract) ----
        api.MapPut("/endpoints/{endpointId}/subscriptions/{tag}", async (string endpointId, string tag, HttpContext ctx, EndpointService endpoints, CancellationToken ct) =>
        {
            var ep = await endpoints.ResolveForSubscriptionsAsync(ctx.User.TenantId(), endpointId, ctx.User.BoundEndpointRef(), ct);
            await endpoints.AddSubscriptionsAsync(ep, [tag], ct);
            return Results.NoContent();
        }).RequireAuthorization(ApiAuth.PolicySubscribe);

        api.MapDelete("/endpoints/{endpointId}/subscriptions/{tag}", async (string endpointId, string tag, HttpContext ctx, EndpointService endpoints, CancellationToken ct) =>
        {
            var ep = await endpoints.ResolveForSubscriptionsAsync(ctx.User.TenantId(), endpointId, ctx.User.BoundEndpointRef(), ct);
            await endpoints.RemoveSubscriptionsAsync(ep, [tag], ct);
            return Results.NoContent();
        }).RequireAuthorization(ApiAuth.PolicySubscribe);

        return app;
    }

    private static IResult Health() => Results.Ok(new HealthResponse("ok", Version, DateTimeOffset.UtcNow));
}

/// <summary>Translates <see cref="ApiException"/> and malformed JSON into the error envelope.</summary>
public sealed class ApiExceptionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        try
        {
            return await next(ctx);
        }
        catch (ApiException ex)
        {
            return Results.Json(ApiErrors.Build(ctx.HttpContext, ex.Code, ex.Message, ex.Details), statusCode: ex.Status);
        }
        // Body-binding failures (malformed JSON, missing body, wrong Content-Type) throw BadHttpRequestException before
        // filters run; the /api exception handler in Program.cs turns those into the JSON envelope.
    }
}
