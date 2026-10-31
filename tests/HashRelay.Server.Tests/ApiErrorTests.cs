using System.Net;
using System.Net.Http.Json;
using System.Text;
using HashRelay.Contracts;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>
/// Every problem on a machine-facing path comes back in the JSON error envelope, in Production as well as Development:
/// a malformed body, a missing content type, an unknown route. Web pages keep the HTML error page.
/// </summary>
public sealed class ApiErrorTests : IAsyncLifetime
{
    private readonly RelayFactory _factory = new() { HostEnvironment = "Production" };
    private RelayFactory.Workspace _ws = null!;

    public async ValueTask InitializeAsync() => _ws = await _factory.CreateWorkspaceAsync("Errors");
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    private static async Task<ApiError> Envelope(HttpResponseMessage res)
    {
        Assert.StartsWith("application/json", res.Content.Headers.ContentType?.MediaType);
        var err = await res.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(err);
        Assert.False(string.IsNullOrEmpty(err!.TraceId));
        return err;
    }

    [Fact]
    public async Task Malformed_json_body_is_a_json_400()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        // What cmd.exe sends when the body is wrapped in single quotes.
        var res = await c.PostAsync("/api/v1/notifications", new StringContent("'{tags:[builds],title:Hello", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(ErrorCodes.Validation, (await Envelope(res)).Code);
    }

    [Fact]
    public async Task Empty_body_is_a_json_400()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        var res = await c.PostAsync("/api/v1/notifications", new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(ErrorCodes.Validation, (await Envelope(res)).Code);
    }

    [Fact]
    public async Task Missing_content_type_is_a_json_415()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        var content = new StringContent("{\"tags\":[\"builds\"],\"title\":\"x\"}");
        content.Headers.ContentType = null;
        var res = await c.PostAsync("/api/v1/notifications", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
        var err = await Envelope(res);
        Assert.Equal(ErrorCodes.Validation, err.Code);
        Assert.Contains("application/json", err.Message);
    }

    [Fact]
    public async Task Unknown_api_route_is_a_json_404()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        var res = await c.GetAsync("/api/v1/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, (await Envelope(res)).Code);
    }

    [Fact]
    public async Task Missing_credential_is_a_json_401()
    {
        var res = await _factory.ApiClient(null).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(ErrorCodes.Unauthenticated, (await Envelope(res)).Code);
    }

    [Fact]
    public async Task Web_404_stays_an_html_page()
    {
        var res = await _factory.CreateClient().GetAsync("/no-such-page");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.StartsWith("text/html", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Page not found", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Valid_publish_still_works_in_production_mode()
    {
        var c = _factory.ApiClient(_ws.ApiKey);
        var res = await c.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["builds"], "Hello from the API", "Your first HashRelay notification"));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
    }
}
