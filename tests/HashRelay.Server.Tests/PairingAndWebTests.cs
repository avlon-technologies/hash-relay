using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using HashRelay.Contracts;
using HashRelay.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>Device pairing lifecycle, device-token scoping and the public web pages.</summary>
public sealed class PairingAndWebTests : IAsyncLifetime
{
    private readonly RelayFactory _factory = new();
    private RelayFactory.Workspace _a = null!;

    public async ValueTask InitializeAsync() => _a = await _factory.CreateWorkspaceAsync("Pair Co");
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    [Fact]
    public async Task Device_pairing_issues_endpoint_bound_token_once()
    {
        var anon = _factory.ApiClient(null);
        var code = await (await anon.PostAsJsonAsync("/api/v1/device/code", new DeviceCodeRequest("laptop-1", "Laptop", "windows", "0.1.0", "agent"))).ReadJson<DeviceCodeResponse>();
        Assert.Matches(new Regex("^[A-Z2-9]{4}-[A-Z2-9]{4}$"), code.UserCode);
        Assert.Contains("/pair?code=" + code.UserCode, code.VerificationUrlComplete);

        var pending = await (await anon.PostAsJsonAsync("/api/v1/device/token", new DeviceTokenRequest(code.DeviceCode))).ReadJson<DeviceTokenResponse>();
        Assert.Equal("pending", pending.Status);

        // approve as the workspace owner (what the /pair page does)
        using (var scope = _factory.Services.CreateScope())
        {
            var pairing = scope.ServiceProvider.GetRequiredService<DevicePairingService>();
            var p = await pairing.FindPendingAsync(code.UserCode.ToLowerInvariant().Replace("-", ""), CancellationToken.None);
            Assert.NotNull(p);
            await pairing.ApproveAsync(p!.Id, _a.AccountId, _a.TenantId, [Scopes.Subscribe], CancellationToken.None);
        }

        var approved = await (await anon.PostAsJsonAsync("/api/v1/device/token", new DeviceTokenRequest(code.DeviceCode))).ReadJson<DeviceTokenResponse>();
        Assert.Equal("approved", approved.Status);
        Assert.StartsWith("hr_dev_", approved.Token);
        Assert.Equal(_a.TenantId, approved.Tenant!.Id);
        Assert.Equal(CredentialKinds.Device, approved.Credential!.Kind);

        // token is handed out exactly once
        var again = await (await anon.PostAsJsonAsync("/api/v1/device/token", new DeviceTokenRequest(code.DeviceCode))).ReadJson<DeviceTokenResponse>();
        Assert.Equal("expired", again.Status);

        // the device token can act as its own endpoint...
        var dev = _factory.ApiClient(approved.Token);
        var subs = await dev.PutAsJsonAsync("/api/v1/endpoints/laptop-1/subscriptions", new SubscriptionsRequest(["builds"]));
        Assert.Equal(HttpStatusCode.OK, subs.StatusCode);

        // ...but not as another endpoint, and it cannot publish
        var other = await dev.PutAsJsonAsync("/api/v1/endpoints/someone-else/subscriptions", new SubscriptionsRequest(["builds"]));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        var pub = await dev.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["builds"], "nope"));
        Assert.Equal(HttpStatusCode.Forbidden, pub.StatusCode);

        // endpoint listing is scoped to the bound endpoint
        var list = await (await dev.GetAsync("/api/v1/endpoints")).ReadJson<List<EndpointInfo>>();
        Assert.Single(list);
        Assert.Equal("laptop-1", list[0].EndpointId);
    }

    [Fact]
    public async Task Unknown_device_code_reports_expired()
    {
        var anon = _factory.ApiClient(null);
        var res = await (await anon.PostAsJsonAsync("/api/v1/device/token", new DeviceTokenRequest("nope"))).ReadJson<DeviceTokenResponse>();
        Assert.Equal("expired", res.Status);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/signup")]
    [InlineData("/login")]
    [InlineData("/forgot-password")]
    [InlineData("/reset-password?token=nope")]
    [InlineData("/docs")]
    [InlineData("/downloads")]
    public async Task Public_pages_render(string path)
    {
        var c = _factory.CreateClient();
        var res = await c.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("HashRelay", html);
    }

    [Fact]
    public async Task Dashboard_requires_sign_in()
    {
        var c = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var res = await c.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Contains("/login", res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Signup_then_dashboard_and_key_creation_work_through_the_browser_flow()
    {
        var c = _factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        var page = await c.GetStringAsync("/signup");
        var token = AntiForgery(page);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Name"] = "Ada", ["Input.Email"] = $"ada-{Guid.NewGuid():n}@test.local", ["Input.Password"] = "a-long-password-1",
            ["Input.Workspace"] = "Analytical Engines", ["__RequestVerificationToken"] = token,
        });
        var res = await c.PostAsync("/signup", form);
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.StartsWith("/dashboard", res.Headers.Location!.ToString());

        var dash = await c.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.OK, dash.StatusCode);
        var dashHtml = await dash.Content.ReadAsStringAsync();
        Assert.Contains("Analytical Engines", dashHtml);

        var keysPage = await c.GetStringAsync("/dashboard/keys");
        var create = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["label"] = "CI", ["scopes"] = "publish", ["expiresDays"] = "0", ["__RequestVerificationToken"] = AntiForgery(keysPage),
        });
        var created = await c.PostAsync("/dashboard/keys?handler=Create", create);
        Assert.Equal(HttpStatusCode.Found, created.StatusCode);
        var after = await c.GetStringAsync("/dashboard/keys");
        var secret = Regex.Match(after, "hr_live_[A-Za-z0-9]{40}").Value;
        Assert.False(string.IsNullOrEmpty(secret));

        // the freshly minted key publishes
        var api = _factory.ApiClient(secret);
        var pub = await api.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["hello"], "From the dashboard key"));
        Assert.Equal(HttpStatusCode.Accepted, pub.StatusCode);

        var live = await c.GetAsync("/dashboard/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    private static string AntiForgery(string html)
        => Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
}
