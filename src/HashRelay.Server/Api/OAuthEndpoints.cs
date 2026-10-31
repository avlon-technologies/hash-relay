using System.Text.Json;
using System.Text.Json.Serialization;
using HashRelay.Contracts;
using HashRelay.Server.Pages.Dashboard;
using HashRelay.Server.Services;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Api;

/// <summary>
/// OAuth 2.1 metadata, dynamic client registration, token and revocation endpoints (RFC 8414, 9728, 7591, 6749, 7009).
/// The authorization endpoint is the Razor page at /oauth/authorize, which needs the dashboard session.
/// </summary>
public static class OAuthEndpoints
{
    public const string CorsPolicy = "oauth";

    public sealed record RegistrationRequest(
        [property: JsonPropertyName("client_name")] string? ClientName,
        [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string>? RedirectUris,
        [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod,
        [property: JsonPropertyName("client_uri")] string? ClientUri,
        [property: JsonPropertyName("logo_uri")] string? LogoUri,
        [property: JsonPropertyName("grant_types")] IReadOnlyList<string>? GrantTypes,
        [property: JsonPropertyName("response_types")] IReadOnlyList<string>? ResponseTypes,
        [property: JsonPropertyName("scope")] string? Scope);

    public static IEndpointRouteBuilder MapOAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-authorization-server", (PublicBase pb) => Results.Json(AuthorizationServerMetadata(pb.Url)))
            .AllowAnonymous().RequireCors(CorsPolicy);
        app.MapGet("/.well-known/oauth-authorization-server/{**rest}", (PublicBase pb) => Results.Json(AuthorizationServerMetadata(pb.Url)))
            .AllowAnonymous().RequireCors(CorsPolicy);

        app.MapGet("/.well-known/oauth-protected-resource", (PublicBase pb, IOptions<HashRelayOptions> o) => Results.Json(ProtectedResourceMetadata(pb.Url, o.Value)))
            .AllowAnonymous().RequireCors(CorsPolicy);
        app.MapGet("/.well-known/oauth-protected-resource/{**rest}", (PublicBase pb, IOptions<HashRelayOptions> o) => Results.Json(ProtectedResourceMetadata(pb.Url, o.Value)))
            .AllowAnonymous().RequireCors(CorsPolicy);

        app.MapPost("/oauth/register", async (HttpContext ctx, OAuthService oauth, CancellationToken ct) =>
        {
            RegistrationRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<RegistrationRequest>(ct); }
            catch (JsonException) { return Error("invalid_client_metadata", "The body is not valid JSON."); }
            if (req is null) return Error("invalid_client_metadata", "A JSON body is required.");
            try
            {
                var (client, secret) = await oauth.RegisterAsync(new OAuthService.ClientRegistration(req.ClientName, req.RedirectUris, req.TokenEndpointAuthMethod, req.ClientUri, req.LogoUri), ct);
                var body = new Dictionary<string, object?>
                {
                    ["client_id"] = client.Id,
                    ["client_id_issued_at"] = client.CreatedAt.ToUnixTimeSeconds(),
                    ["client_name"] = client.Name,
                    ["redirect_uris"] = client.RedirectUriList,
                    ["token_endpoint_auth_method"] = client.AuthMethod,
                    ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                    ["response_types"] = new[] { "code" },
                    ["scope"] = string.Join(' ', ServiceScopes.All),
                };
                if (client.ClientUri is not null) body["client_uri"] = client.ClientUri;
                if (client.LogoUri is not null) body["logo_uri"] = client.LogoUri;
                if (secret is not null) { body["client_secret"] = secret; body["client_secret_expires_at"] = 0; }
                return Results.Json(body, statusCode: StatusCodes.Status201Created);
            }
            catch (OAuthException ex) { return Error(ex); }
        }).AllowAnonymous().RequireRateLimiting("pairing").RequireCors(CorsPolicy).DisableAntiforgery();

        app.MapPost("/oauth/token", async (HttpContext ctx, OAuthService oauth, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.Pragma = "no-cache";
            if (!ctx.Request.HasFormContentType) return Error("invalid_request", "Send application/x-www-form-urlencoded.");
            var form = await ctx.Request.ReadFormAsync(ct);
            string? F(string name) => form.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v.ToString() : null;
            try
            {
                var response = F("grant_type") switch
                {
                    "authorization_code" => await oauth.ExchangeCodeAsync(F("code"), F("client_id"), F("client_secret"), F("redirect_uri"), F("code_verifier"), ct),
                    "refresh_token" => await oauth.RefreshAsync(F("refresh_token"), F("client_id"), F("client_secret"), ct),
                    null => throw new OAuthException("invalid_request", "grant_type is required."),
                    _ => throw new OAuthException("unsupported_grant_type", "Use authorization_code or refresh_token."),
                };
                return Results.Json(new Dictionary<string, object?>
                {
                    ["access_token"] = response.AccessToken,
                    ["token_type"] = response.TokenType,
                    ["expires_in"] = response.ExpiresIn,
                    ["refresh_token"] = response.RefreshToken,
                    ["scope"] = response.Scope,
                });
            }
            catch (OAuthException ex) { return Error(ex); }
        }).AllowAnonymous().RequireRateLimiting("auth").RequireCors(CorsPolicy).DisableAntiforgery();

        app.MapPost("/oauth/revoke", async (HttpContext ctx, OAuthService oauth, CancellationToken ct) =>
        {
            if (!ctx.Request.HasFormContentType) return Error("invalid_request", "Send application/x-www-form-urlencoded.");
            var form = await ctx.Request.ReadFormAsync(ct);
            string? F(string name) => form.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v.ToString() : null;
            try { await oauth.RevokeTokenAsync(F("token"), F("client_id"), F("client_secret"), ct); return Results.Ok(); }
            catch (OAuthException ex) { return Error(ex); }
        }).AllowAnonymous().RequireRateLimiting("auth").RequireCors(CorsPolicy).DisableAntiforgery();

        return app;
    }

    public static Dictionary<string, object?> AuthorizationServerMetadata(string issuer) => new()
    {
        ["issuer"] = issuer,
        ["authorization_endpoint"] = issuer + "/oauth/authorize",
        ["token_endpoint"] = issuer + "/oauth/token",
        ["registration_endpoint"] = issuer + "/oauth/register",
        ["revocation_endpoint"] = issuer + "/oauth/revoke",
        ["scopes_supported"] = ServiceScopes.All,
        ["response_types_supported"] = new[] { "code" },
        ["response_modes_supported"] = new[] { "query" },
        ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
        ["code_challenge_methods_supported"] = new[] { "S256" },
        ["token_endpoint_auth_methods_supported"] = new[] { OAuthService.AuthNone, OAuthService.AuthSecretPost },
        ["revocation_endpoint_auth_methods_supported"] = new[] { OAuthService.AuthNone, OAuthService.AuthSecretPost },
        ["service_documentation"] = issuer + "/docs#mcp",
    };

    public static Dictionary<string, object?> ProtectedResourceMetadata(string publicUrl, HashRelayOptions options) => new()
    {
        ["resource"] = McpUrlHelper.Resolve(options, publicUrl),
        ["authorization_servers"] = new[] { publicUrl },
        ["scopes_supported"] = ServiceScopes.All,
        ["bearer_methods_supported"] = new[] { "header" },
        ["resource_name"] = "HashRelay MCP",
        ["resource_documentation"] = publicUrl + "/docs#mcp",
    };

    private static IResult Error(OAuthException ex) => Error(ex.Error, ex.Message, ex.Status);

    private static IResult Error(string error, string description, int status = 400)
        => Results.Json(new Dictionary<string, object?> { ["error"] = error, ["error_description"] = description }, statusCode: status);
}
