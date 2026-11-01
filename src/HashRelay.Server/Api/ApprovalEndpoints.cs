using System.Security.Claims;
using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Services;
using HashRelay.Server.Services.Approvals;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace HashRelay.Server.Api;

/// <summary>
/// Actionable approvals, HTTP API v1 (docs/ACTIONABLE-APPROVALS.md, ADL ApprovalService/ApprovalApi).
/// <list type="bullet">
/// <item>Requester routes (create, get, cancel) take an API key with the publish scope or a service key with
/// <c>approvals:create</c>. Device tokens cannot request: a paired device is a person's receiver, not an application.</item>
/// <item>The decision route takes only a signed-in person's session cookie plus an antiforgery token. No API key,
/// service key, OAuth token or device token is accepted there, whatever its scopes.</item>
/// <item>One-click decisions (<c>/device-decision</c>) take a paired device token AND that device's decision grant
/// secret (<c>HashRelay-Decision-Grant</c>), which only the person who paired it can issue after signing in. The
/// device token alone never decides (ADL requirement DeviceDecisionAuthority).</item>
/// </list>
/// </summary>
public static class ApprovalEndpoints
{
    public const string PolicyRequester = "api:approvals";
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string PolicyDevice = "api:device-decisions";

    public static bool MayRequestApprovals(ClaimsPrincipal user) => user.CredentialKind() switch
    {
        CredentialKinds.Api => user.HasClaim(ApiAuth.ClaimScope, Scopes.Publish),
        CredentialKinds.Service => user.HasClaim(ApiAuth.ClaimScope, ServiceScopes.ApprovalsCreate),
        _ => false,
    };

    public static ApprovalRequesterPrincipal Requester(ClaimsPrincipal user)
    {
        var policy = ServicePolicy.Of(user);
        var publisher = PublisherIdentity.Of(user) ?? new PublisherIdentity(user.CredentialId(), "", PublisherTypes.Unknown);
        return new ApprovalRequesterPrincipal(user.TenantId(), user.CredentialId(), policy?.ServiceAccountId, publisher, policy);
    }

    public static IEndpointRouteBuilder MapApprovalApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/approvals").AddEndpointFilter<ApiExceptionFilter>();

        api.MapPost("", async (CreateApprovalRequest req, HttpContext ctx, ApprovalService approvals, CancellationToken ct) =>
        {
            var (resource, created) = await approvals.CreateAsync(Requester(ctx.User), ctx.Request.Headers[IdempotencyHeader].ToString(), req,
                ctx.TraceIdentifier, ct);
            return created
                ? Results.Created($"/api/v1/approvals/{resource.Id}", resource)
                : Results.Ok(resource);
        }).RequireAuthorization(PolicyRequester).RequireRateLimiting("publish");

        api.MapGet("/{id:guid}", async (Guid id, HttpContext ctx, ApprovalService approvals, CancellationToken ct) =>
            Results.Ok(await approvals.GetForRequesterAsync(Requester(ctx.User), id, ct)))
            .RequireAuthorization(PolicyRequester);

        api.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext ctx, ApprovalService approvals, CancellationToken ct) =>
            Results.Ok(await approvals.CancelAsync(Requester(ctx.User), id, ctx.TraceIdentifier, ct)))
            .RequireAuthorization(PolicyRequester);

        // Authenticated here rather than by policy so an anonymous call gets the 401 envelope, not a login redirect,
        // and so only the cookie scheme is ever consulted.
        api.MapPost("/{id:guid}/decision", async (Guid id, [FromBody] ApprovalDecisionRequest req, HttpContext ctx, IAntiforgery antiforgery,
            ApprovalService approvals, CancellationToken ct) =>
        {
            var session = await ctx.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var accountId = CurrentTenant.AccountIdOf(session.Principal);
            if (!session.Succeeded || accountId is null)
                return Results.Json(ApiErrors.Build(ctx, ErrorCodes.Unauthenticated, "Sign in to HashRelay to decide an approval."), statusCode: 401);
            ctx.User = session.Principal!;
            try { await antiforgery.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException)
            {
                return ApiErrors.Forbidden(ctx, "Missing or invalid antiforgery token.");
            }
            return Results.Ok(await approvals.DecideAsync(accountId.Value, id, req, ctx.Request.Headers[IdempotencyHeader].ToString(), ctx.TraceIdentifier, ct));
        }).AllowAnonymous().RequireRateLimiting("decide");

        api.MapPost("/{id:guid}/device-decision", async (Guid id, [FromBody] DeviceDecisionRequest req, HttpContext ctx, ApprovalService approvals, CancellationToken ct) =>
            Results.Ok(await approvals.DecideFromDeviceAsync(ctx.User.TenantId(), ctx.User.CredentialId(), ctx.User.BoundEndpointRef(), ctx.Request.Headers[DecisionGrants.Header].ToString(),
                id, req, ctx.Request.Headers[IdempotencyHeader].ToString(), ctx.TraceIdentifier, ct)))
            .RequireAuthorization(PolicyDevice).RequireRateLimiting("decide");

        // Enrolment (EnrollDevice) and revocation (RevokeDevice) of one-click approvals, from the device side.
        var grants = app.MapGroup("/api/v1/decision-grants").AddEndpointFilter<ApiExceptionFilter>().RequireAuthorization(PolicyDevice);
        grants.MapPost("", async (HttpContext ctx, ApprovalService approvals, CancellationToken ct) =>
            Results.Ok(await approvals.BeginDecisionGrantAsync(ctx.User.TenantId(), ctx.User.CredentialId(), ct))).RequireRateLimiting("pairing");
        grants.MapPost("/poll", async (DecisionGrantPollRequest req, HttpContext ctx, ApprovalService approvals, CancellationToken ct) =>
            Results.Ok(await approvals.PollDecisionGrantAsync(ctx.User.TenantId(), ctx.User.CredentialId(), req.PollCode, ct)));
        grants.MapDelete("/current", async (HttpContext ctx, ApprovalService approvals, CancellationToken ct) =>
        {
            await approvals.RevokeDecisionGrantFromDeviceAsync(ctx.User.TenantId(), ctx.User.CredentialId(), ctx.Request.Headers[DecisionGrants.Header].ToString(), ct);
            return Results.NoContent();
        });

        return app;
    }
}
