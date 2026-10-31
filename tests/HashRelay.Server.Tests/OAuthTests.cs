using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HashRelay.Server.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>Walks the OAuth 2.1 flow a hosted MCP client (claude.ai, ChatGPT) performs: discovery, registration, consent, PKCE exchange, refresh, revocation.</summary>
public sealed class OAuthTests : IAsyncLifetime
{
    private readonly RelayFactory _factory = new();
    private RelayFactory.Workspace _ws = null!;
    private const string Redirect = "https://client.example/callback";

    public async ValueTask InitializeAsync() => _ws = await _factory.CreateWorkspaceAsync("OAuth Co");
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private async Task<string> RegisterAsync(string name = "Claude", string? authMethod = null)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/oauth/register", new { client_name = name, redirect_uris = new[] { Redirect }, token_endpoint_auth_method = authMethod });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("client_id").GetString()!;
        Assert.StartsWith("hrc_", id);
        return id;
    }

    private static string Token(string html) => System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    /// <summary>Signs in, opens the consent page and approves it. Returns the authorization code from the redirect.</summary>
    private async Task<(string Code, HttpClient Browser)> ConsentAsync(string clientId, string challenge, string scope = "notifications:publish endpoints:read", string allowedTags = "*")
    {
        var browser = await _factory.LoginAsync(_ws);
        var url = QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["redirect_uri"] = Redirect, ["response_type"] = "code", ["scope"] = scope, ["state"] = "xyz",
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["resource"] = _factory.Server.BaseAddress + "mcp",
        });
        var page = await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("wants to connect", html);
        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId), new("redirect_uri", Redirect), new("response_type", "code"), new("scope", scope), new("state", "xyz"),
            new("code_challenge", challenge), new("code_challenge_method", "S256"), new("tenantId", _ws.TenantId.ToString()),
            new("allowedTags", allowedTags), new("hourlyLimit", "60"), new("__RequestVerificationToken", Token(html)),
        };
        foreach (var s in scope.Split(' ')) form.Add(new("scopes", s));
        var res = await browser.PostAsync("/oauth/authorize?handler=Approve", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        var location = res.Headers.Location!;
        Assert.StartsWith(Redirect, location.ToString());
        var q = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("xyz", q["state"]);
        return (q["code"].ToString(), browser);
    }

    private async Task<JsonElement> TokenAsync(Dictionary<string, string> form, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsync("/oauth/token", new FormUrlEncodedContent(form));
        Assert.Equal(expected, res.StatusCode);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Dictionary<string, string> CodeGrant(string code, string clientId, string verifier) => new()
    {
        ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
    };

    private static Dictionary<string, string> RefreshGrant(string refresh, string clientId) => new()
    {
        ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = clientId,
    };

    [Fact]
    public async Task Metadata_documents_the_server_and_the_mcp_resource()
    {
        var c = _factory.CreateClient();
        var auth = await c.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        Assert.EndsWith("/oauth/authorize", auth.GetProperty("authorization_endpoint").GetString());
        Assert.EndsWith("/oauth/token", auth.GetProperty("token_endpoint").GetString());
        Assert.EndsWith("/oauth/register", auth.GetProperty("registration_endpoint").GetString());
        Assert.Contains("S256", auth.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));

        var res = await c.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");
        Assert.EndsWith("/mcp", res.GetProperty("resource").GetString());
        Assert.Equal(auth.GetProperty("issuer").GetString(), res.GetProperty("authorization_servers")[0].GetString());

        // The 401 from /mcp points at the resource metadata (RFC 9728) so clients can start the flow.
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var unauthorized = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Contains("resource_metadata=", unauthorized.Headers.WwwAuthenticate.ToString());

        // CORS preflight from a browser-based client is answered.
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/oauth/token");
        preflight.Headers.Add("Origin", "https://inspector.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        var pf = await c.SendAsync(preflight);
        Assert.Equal("*", pf.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Registration_validates_redirect_uris()
    {
        var c = _factory.CreateClient();
        var bad = await c.PostAsJsonAsync("/oauth/register", new { client_name = "x", redirect_uris = new[] { "http://evil.example/cb" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_redirect_uri", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var loopback = await c.PostAsJsonAsync("/oauth/register", new { client_name = "cli", redirect_uris = new[] { "http://127.0.0.1:8899/cb" } });
        Assert.Equal(HttpStatusCode.Created, loopback.StatusCode);

        var secret = await c.PostAsJsonAsync("/oauth/register", new { client_name = "srv", redirect_uris = new[] { Redirect }, token_endpoint_auth_method = "client_secret_post" });
        var body = await secret.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("hrcs_", body.GetProperty("client_secret").GetString());
    }

    [Fact]
    public async Task Consent_code_exchange_token_works_on_mcp_and_shows_up_as_a_service_account()
    {
        var clientId = await RegisterAsync();
        var (verifier, challenge) = Pkce();
        var (code, browser) = await ConsentAsync(clientId, challenge, "notifications:publish notifications:read", "agent-*");
        Assert.StartsWith("hrac_", code);

        var tokens = await TokenAsync(CodeGrant(code, clientId, verifier));
        var access = tokens.GetProperty("access_token").GetString()!;
        Assert.StartsWith("hr_svc_", access);
        Assert.Equal(3600, tokens.GetProperty("expires_in").GetInt32());
        Assert.Contains("notifications:read", tokens.GetProperty("scope").GetString());

        // The token is an ordinary service key: MCP works and the policy applies.
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_factory.Server.BaseAddress, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + access },
        }, _factory.CreateClient());
        await using var mcp = await McpClient.CreateAsync(transport);
        var ok = await mcp.CallToolAsync("publish_notification", new Dictionary<string, object?> { ["tags"] = new[] { "agent-ops" }, ["title"] = "From Claude", ["body"] = "hello" });
        Assert.NotEqual(true, ok.IsError);
        var refused = await mcp.CallToolAsync("publish_notification", new Dictionary<string, object?> { ["tags"] = new[] { "payroll" }, ["title"] = "nope", ["body"] = "x" });
        Assert.Equal(true, refused.IsError);

        // The dashboard shows the connected app on a service account named after it.
        var list = await browser.GetStringAsync("/dashboard/service-accounts");
        Assert.Contains("Claude", list); // the middle dot is HTML-encoded by Razor, so match the name in the database below
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var sa = await db.ServiceAccounts.SingleAsync(s => s.TenantId == _ws.TenantId);
        Assert.Equal("Claude · Tester", sa.Name);
        var detail = await browser.GetStringAsync($"/dashboard/service-accounts/{sa.Id}");
        Assert.Contains("Connected app", detail);
        Assert.Contains("Disconnect app", detail);
        Assert.Contains("App connected", detail);

        // A second consent for the same client reuses the service account instead of creating another.
        var (_, challenge2) = Pkce();
        await ConsentAsync(clientId, challenge2, "notifications:publish", "*");
        Assert.Equal(1, await db.ServiceAccounts.CountAsync(s => s.TenantId == _ws.TenantId));
    }

    [Fact]
    public async Task Code_cannot_be_replayed_and_needs_the_right_verifier()
    {
        var clientId = await RegisterAsync();
        var (verifier, challenge) = Pkce();
        var (code, _) = await ConsentAsync(clientId, challenge);

        var wrong = await TokenAsync(CodeGrant(code, clientId, Pkce().Verifier), HttpStatusCode.BadRequest);
        Assert.Equal("invalid_grant", wrong.GetProperty("error").GetString());

        var good = await TokenAsync(CodeGrant(code, clientId, verifier));
        var access = good.GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await _factory.ApiClient(access).GetAsync("/api/v1/endpoints")).StatusCode);

        // Replaying the code revokes what it issued.
        var replay = await TokenAsync(CodeGrant(code, clientId, verifier), HttpStatusCode.BadRequest);
        Assert.Equal("invalid_grant", replay.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ApiClient(access).GetAsync("/api/v1/endpoints")).StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_and_reuse_of_an_old_refresh_token_revokes_the_family()
    {
        var clientId = await RegisterAsync();
        var (verifier, challenge) = Pkce();
        var (code, _) = await ConsentAsync(clientId, challenge);
        var first = await TokenAsync(CodeGrant(code, clientId, verifier));
        var access1 = first.GetProperty("access_token").GetString()!;
        var refresh1 = first.GetProperty("refresh_token").GetString()!;
        Assert.StartsWith("hrr_", refresh1);

        var second = await TokenAsync(RefreshGrant(refresh1, clientId));
        var access2 = second.GetProperty("access_token").GetString()!;
        var refresh2 = second.GetProperty("refresh_token").GetString()!;
        Assert.NotEqual(refresh1, refresh2);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ApiClient(access1).GetAsync("/api/v1/endpoints")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.ApiClient(access2).GetAsync("/api/v1/endpoints")).StatusCode);

        // Reusing refresh1 is a leak signal: everything is revoked, including access2 and refresh2.
        var reuse = await TokenAsync(RefreshGrant(refresh1, clientId), HttpStatusCode.BadRequest);
        Assert.Equal("invalid_grant", reuse.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ApiClient(access2).GetAsync("/api/v1/endpoints")).StatusCode);
        await TokenAsync(RefreshGrant(refresh2, clientId), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Disconnect_from_the_dashboard_and_revoke_endpoint_both_kill_tokens()
    {
        var clientId = await RegisterAsync("Desktop");
        var (verifier, challenge) = Pkce();
        var (code, browser) = await ConsentAsync(clientId, challenge);
        var t = await TokenAsync(CodeGrant(code, clientId, verifier));
        var access = t.GetProperty("access_token").GetString()!;
        var refresh = t.GetProperty("refresh_token").GetString()!;

        // RFC 7009 revoke of the refresh token takes the access token with it.
        var revoke = await _factory.CreateClient().PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh, ["client_id"] = clientId }));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ApiClient(access).GetAsync("/api/v1/endpoints")).StatusCode);
        await TokenAsync(RefreshGrant(refresh, clientId), HttpStatusCode.BadRequest);

        // Consent again, then disconnect from the dashboard: the grant is revoked and the account disabled.
        var (verifier2, challenge2) = Pkce();
        var (code2, _) = await ConsentAsync(clientId, challenge2);
        var t2 = await TokenAsync(CodeGrant(code2, clientId, verifier2));
        var access2 = t2.GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await _factory.ApiClient(access2).GetAsync("/api/v1/endpoints")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        var sa = await db.ServiceAccounts.SingleAsync(s => s.TenantId == _ws.TenantId);
        var detail = await browser.GetStringAsync($"/dashboard/service-accounts/{sa.Id}");
        var post = await browser.PostAsync($"/dashboard/service-accounts/{sa.Id}?handler=Disconnect", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(detail) }));
        Assert.Equal(HttpStatusCode.Found, post.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ApiClient(access2).GetAsync("/api/v1/endpoints")).StatusCode);
        Assert.False((await db.ServiceAccounts.AsNoTracking().SingleAsync(s => s.Id == sa.Id)).Enabled);
        Assert.Null(await db.OAuthGrants.FirstOrDefaultAsync(g => g.ServiceAccountId == sa.Id && g.RevokedAt == null));
    }

    [Fact]
    public async Task Authorize_rejects_unknown_clients_and_reports_bad_requests_to_the_redirect_uri()
    {
        var browser = await _factory.LoginAsync(_ws);
        var unknown = await browser.GetAsync("/oauth/authorize?client_id=hrc_nope&redirect_uri=" + Uri.EscapeDataString(Redirect) + "&response_type=code");
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Contains("Unknown client", await unknown.Content.ReadAsStringAsync());

        var clientId = await RegisterAsync();
        var noPkce = await browser.GetAsync($"/oauth/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&response_type=code&state=s1");
        Assert.Equal(HttpStatusCode.Found, noPkce.StatusCode);
        var q = QueryHelpers.ParseQuery(noPkce.Headers.Location!.Query);
        Assert.Equal("invalid_request", q["error"]);
        Assert.Equal("s1", q["state"]);

        // Signed-out visitors are sent to login and come back.
        var anon = await _factory.CreateClient(new() { AllowAutoRedirect = false }).GetAsync($"/oauth/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&response_type=code");
        Assert.Equal(HttpStatusCode.Found, anon.StatusCode);
        Assert.Contains("/login", anon.Headers.Location!.ToString());
    }
}
