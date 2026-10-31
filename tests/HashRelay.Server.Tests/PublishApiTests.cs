using System.Net;
using System.Net.Http.Json;
using HashRelay.Contracts;
using Xunit;

namespace HashRelay.Server.Tests;

public sealed class PublishApiTests : IAsyncLifetime
{
    private readonly RelayFactory _factory = new();
    private RelayFactory.Workspace _ws = null!;

    public async ValueTask InitializeAsync() => _ws = await _factory.CreateWorkspaceAsync("Acme");
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    [Fact]
    public async Task Health_is_anonymous_on_both_routes()
    {
        var c = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/healthz")).StatusCode);
        var live = await c.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("ok", (await live.ReadJson<HealthResponse>()).Status);
    }

    [Fact]
    public async Task Publish_without_credential_is_401_with_envelope()
    {
        var c = _factory.ApiClient(null);
        var res = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["demo"], "t"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var err = await res.ReadJson<ApiError>();
        Assert.Equal(ErrorCodes.Unauthenticated, err.Code);
        Assert.False(string.IsNullOrEmpty(err.TraceId));
    }

    [Fact]
    public async Task Publish_with_garbage_key_is_401()
    {
        var c = _factory.ApiClient("hr_live_" + new string('x', 40));
        var res = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["demo"], "t"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Publish_with_subscribe_only_key_is_403()
    {
        var c = _factory.ApiClient(_ws.SubscribeOnlyKey);
        var res = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["demo"], "t"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, (await res.ReadJson<ApiError>()).Code);
    }

    [Fact]
    public async Task Publish_to_unsubscribed_tag_is_accepted_with_zero_matches()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        var res = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["#Nobody"], "Hello", "world"));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var r = await res.ReadJson<PublishResult>();
        Assert.Equal(0, r.MatchedEndpoints);
        Assert.Equal(["nobody"], r.Tags);
        Assert.NotEqual(Guid.Empty, r.NotificationId);
    }

    [Fact]
    public async Task Validation_errors_use_envelope_with_details()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        var res = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["bad tag!"], new string('x', 500)));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var err = await res.ReadJson<ApiError>();
        Assert.Equal(ErrorCodes.Validation, err.Code);
        Assert.NotNull(err.Details);
        Assert.True(err.Details!.ContainsKey("tags"));
        Assert.True(err.Details!.ContainsKey("title"));
    }

    [Fact]
    public async Task Too_many_tags_is_rejected()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        var tags = Enumerable.Range(0, 11).Select(i => $"t{i}").ToList();
        var res = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(tags, "x"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Idempotent_republish_returns_200_duplicate_and_changed_payload_is_409()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        var id = Guid.NewGuid();
        var first = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["demo"], "Same", "body", NotificationId: id));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var again = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["demo"], "Same", "body", NotificationId: id));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True((await again.ReadJson<PublishResult>()).Duplicate);

        var changed = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["demo"], "Different", "body", NotificationId: id));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(ErrorCodes.Conflict, (await changed.ReadJson<ApiError>()).Code);
    }

    [Fact]
    public async Task Me_returns_tenant_and_credential()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        var me = await (await c.GetAsync("/api/v1/me")).ReadJson<MeResponse>();
        Assert.Equal(_ws.TenantId, me.Tenant.Id);
        Assert.Equal(CredentialKinds.Api, me.Credential.Kind);
        Assert.Contains(Scopes.Publish, me.Credential.Scopes);
    }
}
