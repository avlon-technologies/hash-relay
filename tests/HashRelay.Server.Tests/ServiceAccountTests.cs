using System.Net;
using System.Net.Http.Json;
using HashRelay.Contracts;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using HashRelay.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HashRelay.Server.Tests;

public sealed class ServiceAccountTests : IAsyncLifetime
{
    private readonly RelayFactory _factory = new();
    private RelayFactory.Workspace _ws = null!;

    public async ValueTask InitializeAsync() => _ws = await _factory.CreateWorkspaceAsync("Agents Inc");
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    private async Task<(ServiceAccount Account, string Key)> CreateAsync(string name, string[] scopes, string tags = "agent-*", string? hosts = null, int hourly = 60)
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ServiceAccountService>();
        var draft = ServiceAccountService.Validate(name, null, scopes, tags, hosts, hourly);
        var account = await svc.CreateAsync(_ws.TenantId, _ws.AccountId, draft, CancellationToken.None);
        var (_, key) = await svc.IssueKeyAsync(_ws.TenantId, account.Id, _ws.AccountId, null, null, CancellationToken.None);
        return (account, key);
    }

    private async Task<List<AuditEvent>> AuditAsync(Guid serviceAccountId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        return await db.AuditEvents.Where(a => a.ServiceAccountId == serviceAccountId).OrderBy(a => a.At).ToListAsync();
    }

    [Fact]
    public void Validation_normalises_patterns_and_rejects_garbage()
    {
        var d = ServiceAccountService.Validate("bot", null, [ServiceScopes.Publish], "#Agent-*, ci/*  builds", "https://GitHub.com/, status.example.com", 60);
        Assert.Equal(["agent-*", "ci/*", "builds"], d.AllowedTags);
        Assert.Equal(["github.com", "status.example.com"], d.AllowedUrlHosts);
        Assert.Throws<ApiException>(() => ServiceAccountService.Validate("bot", null, [ServiceScopes.Publish], "", null, 60));
        Assert.Throws<ApiException>(() => ServiceAccountService.Validate("bot", null, [], "agent-*", null, 60));
        Assert.Throws<ApiException>(() => ServiceAccountService.Validate("bot", null, ["admin:everything"], "agent-*", null, 60));
        Assert.Throws<ApiException>(() => ServiceAccountService.Validate("bot", null, [ServiceScopes.Publish], "bad tag!", null, 60));
    }

    [Fact]
    public async Task Service_key_authenticates_with_kind_service_and_implied_publish_scope()
    {
        var (account, key) = await CreateAsync("claude", [ServiceScopes.Publish]);
        var me = await _factory.ApiClient(key).GetFromJsonAsync<MeResponse>("/api/v1/me");
        Assert.NotNull(me);
        Assert.Equal(CredentialKinds.Service, me!.Credential.Kind);
        Assert.Equal(_ws.TenantId, me.Tenant.Id);

        var audit = await AuditAsync(account.Id);
        Assert.Contains(audit, a => a.Action == AuditActions.ServiceAccountCreated);
        Assert.Contains(audit, a => a.Action == AuditActions.KeyIssued && a.Target!.StartsWith("hr_svc_"));
        Assert.DoesNotContain(audit, a => a.Detail is not null && a.Detail.Contains(key));
    }

    [Fact]
    public async Task Publish_is_limited_to_allowed_hashtags_and_https_links_and_is_audited()
    {
        var (account, key) = await CreateAsync("claude", [ServiceScopes.Publish], tags: "agent-*", hosts: "github.com");
        var c = _factory.ApiClient(key);

        var ok = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["#Agent-Reports"], "Report", Url: "https://github.com/avlon/x"));
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);

        var badTag = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["agent-reports", "alerts"], "Nope"));
        Assert.Equal(HttpStatusCode.Forbidden, badTag.StatusCode);
        var err = await badTag.ReadJson<ApiError>();
        Assert.Equal(ErrorCodes.Forbidden, err.Code);
        Assert.Contains("#alerts", err.Message);

        var httpUrl = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["agent-reports"], "Nope", Url: "http://github.com/x"));
        Assert.Equal(HttpStatusCode.Forbidden, httpUrl.StatusCode);

        var otherHost = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["agent-reports"], "Nope", Url: "https://evil.example/x"));
        Assert.Equal(HttpStatusCode.Forbidden, otherHost.StatusCode);

        var audit = await AuditAsync(account.Id);
        Assert.Single(audit, a => a.Action == AuditActions.Published);
        Assert.Equal(3, audit.Count(a => a.Action == AuditActions.PublishRefused));
        Assert.All(audit.Where(a => a.Action is AuditActions.Published or AuditActions.PublishRefused), a => Assert.Equal("api", a.Source));
    }

    [Fact]
    public async Task Hourly_quota_is_shared_across_the_accounts_keys()
    {
        var (account, key1) = await CreateAsync("busy", [ServiceScopes.Publish], hourly: 2);
        string key2;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ServiceAccountService>();
            (_, key2) = await svc.IssueKeyAsync(_ws.TenantId, account.Id, _ws.AccountId, "second", null, CancellationToken.None);
        }
        Assert.Equal(HttpStatusCode.Accepted, (await _factory.ApiClient(key1).PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["agent-a"], "1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await _factory.ApiClient(key2).PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["agent-a"], "2"))).StatusCode);
        var third = await _factory.ApiClient(key1).PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["agent-a"], "3"));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(ErrorCodes.Throttled, (await third.ReadJson<ApiError>()).Code);
    }

    [Fact]
    public async Task Read_and_endpoint_scopes_gate_the_read_routes()
    {
        var (_, publishOnly) = await CreateAsync("writer", [ServiceScopes.Publish]);
        var (_, reader) = await CreateAsync("reader", [ServiceScopes.Read, ServiceScopes.Endpoints]);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ApiClient(publishOnly).GetAsync("/api/v1/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ApiClient(publishOnly).GetAsync("/api/v1/endpoints")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.ApiClient(reader).GetAsync("/api/v1/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.ApiClient(reader).GetAsync("/api/v1/endpoints")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ApiClient(reader).PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["agent-a"], "x"))).StatusCode);
        // Ordinary API keys are not restricted by the fine-grained policies.
        Assert.Equal(HttpStatusCode.OK, (await _factory.ApiClient(_ws.ApiKey).GetAsync("/api/v1/notifications")).StatusCode);
    }

    [Fact]
    public async Task Disabling_the_account_suspends_every_key_and_revoking_a_key_is_immediate()
    {
        var (account, key) = await CreateAsync("flaky", [ServiceScopes.Publish]);
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ServiceAccountService>();

        await svc.SetEnabledAsync(_ws.TenantId, account.Id, _ws.AccountId, false, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ApiClient(key).GetAsync("/api/v1/me")).StatusCode);
        await svc.SetEnabledAsync(_ws.TenantId, account.Id, _ws.AccountId, true, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await _factory.ApiClient(key).GetAsync("/api/v1/me")).StatusCode);

        var keys = await svc.KeysAsync(_ws.TenantId, account.Id, CancellationToken.None);
        await svc.RevokeKeyAsync(_ws.TenantId, account.Id, keys.Single().Id, _ws.AccountId, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ApiClient(key).GetAsync("/api/v1/me")).StatusCode);

        var audit = await AuditAsync(account.Id);
        Assert.Contains(audit, a => a.Action == AuditActions.ServiceAccountDisabled);
        Assert.Contains(audit, a => a.Action == AuditActions.KeyRevoked);
    }

    [Fact]
    public async Task Dashboard_creates_a_service_account_and_shows_the_key_once()
    {
        var browser = await _factory.LoginAsync(_ws);
        var list = await browser.GetAsync("/dashboard/service-accounts");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var html = await list.Content.ReadAsStringAsync();
        Assert.Contains("Service accounts", html);
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

        var create = await browser.PostAsync("/dashboard/service-accounts?handler=Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "cursor", ["description"] = "Cursor on the laptop", ["scopes"] = ServiceScopes.Publish, ["allowedTags"] = "agent-* ci/*",
            ["allowedUrlHosts"] = "", ["hourlyLimit"] = "60", ["expiresDays"] = "90", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Found, create.StatusCode);
        var detailUrl = create.Headers.Location!.ToString();
        Assert.StartsWith("/dashboard/service-accounts/", detailUrl);

        var detail = await browser.GetStringAsync(detailUrl);
        Assert.Contains("hr_svc_", detail);
        Assert.Contains("claude mcp add", detail);
        Assert.Contains("agent-* ci/*", detail);

        // The secret is shown once.
        var again = await browser.GetStringAsync(detailUrl);
        Assert.DoesNotContain("New key</span>", again);
    }
}
