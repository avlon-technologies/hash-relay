using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HashRelay.Contracts;
using HashRelay.Server.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>Markdown bodies, Mermaid diagrams rendered to toast images, image uploads and the notification page.</summary>
public sealed class RichContentTests : IAsyncLifetime
{
    private static readonly byte[] TinyPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01,
        0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
        0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];

    /// <summary>Stands in for mermaid.ink: returns a fixed PNG and counts calls, so tests never touch the network.</summary>
    private sealed class FakeRenderer : IDiagramRenderer
    {
        public int Calls;
        public bool Fail;
        public bool Enabled => true;
        public Task<byte[]?> RenderPngAsync(string mermaidSource, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<byte[]?>(Fail ? null : TinyPng);
        }
    }

    private sealed class Factory : RelayFactory
    {
        public FakeRenderer Renderer { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s => s.AddSingleton<IDiagramRenderer>(Renderer));
        }
    }

    private readonly Factory _factory = new();
    private RelayFactory.Workspace _ws = null!;

    public async ValueTask InitializeAsync() => _ws = await _factory.CreateWorkspaceAsync("Acme");
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    private const string Diagram = "graph TD\n  A[Build] --> B{Tests}\n  B -->|pass| C[Deploy]\n  B -->|fail| D[Fix]";

    [Fact]
    public async Task Diagram_is_rendered_once_and_delivered_as_toast_image_with_detail_url()
    {
        var api = _factory.ApiClient(_ws.ApiKey);
        await api.PutAsync("/api/v1/endpoints/dev-1", JsonContent.Create(new RegisterEndpointRequest("dev-1", "windows", "Dev box")));
        await api.PutAsJsonAsync("/api/v1/endpoints/dev-1/subscriptions", new SubscriptionsRequest(["ci"]));

        await using var hub = _factory.Hub(_ws.ApiKey);
        var received = new TaskCompletionSource<NotificationMessage>();
        hub.On<NotificationMessage>(HubMethods.NotificationReceived, m => received.TrySetResult(m));
        await hub.StartAsync();
        await hub.InvokeAsync<HelloResponse>(HubMethods.Hello, new HelloRequest("dev-1", "Dev box", "windows", "test"));

        var res = await api.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["ci"], "Pipeline", Diagram: Diagram));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var result = await res.ReadJson<PublishResult>();
        Assert.NotNull(result.ImageUrl);
        Assert.StartsWith("http://localhost/i/", result.ImageUrl);
        Assert.Equal($"http://localhost/n/{result.NotificationId}", result.DetailUrl);

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(msg.Rich);
        Assert.Equal(result.ImageUrl, msg.ImageUrl);
        Assert.Equal(result.DetailUrl, msg.DetailUrl);

        // the rendered PNG is served anonymously at the unguessable URL
        var anon = _factory.CreateClient();
        var img = await anon.GetAsync(new Uri(result.ImageUrl!).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, img.StatusCode);
        Assert.Equal("image/png", img.Content.Headers.ContentType!.MediaType);
        Assert.Equal(TinyPng, await img.Content.ReadAsByteArrayAsync());

        // same diagram again: served from the cache, not re-rendered
        await api.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["ci"], "Pipeline again", Diagram: Diagram));
        Assert.Equal(1, _factory.Renderer.Calls);
    }

    [Fact]
    public async Task Markdown_mermaid_fence_becomes_the_diagram_and_body_is_summarised()
    {
        var api = _factory.ApiClient(_ws.ApiKey);
        var md = "# Release 1.2\n\nShipped **three** fixes and a `hotfix`.\n\n```mermaid\nsequenceDiagram\n  A->>B: hi\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |";
        var res = await api.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["rel"], "Release", Markdown: md));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var result = await res.ReadJson<PublishResult>();
        Assert.NotNull(result.ImageUrl); // fence was rendered for the toast
        Assert.Equal(1, _factory.Renderer.Calls);

        var recent = await api.GetFromJsonAsync<List<NotificationSummary>>("/api/v1/notifications");
        var n = Assert.Single(recent!);
        Assert.Equal("Shipped three fixes and a hotfix.", n.Body);
        Assert.True(n.HasMarkdown);
        Assert.True(n.HasDiagram);
        Assert.Equal(result.DetailUrl, n.DetailUrl);
    }

    [Fact]
    public async Task Diagram_render_failure_still_publishes_without_image()
    {
        _factory.Renderer.Fail = true;
        var api = _factory.ApiClient(_ws.ApiKey);
        var res = await api.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["ci"], "No image", Diagram: "graph LR\nA-->B"));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var result = await res.ReadJson<PublishResult>();
        Assert.Null(result.ImageUrl);
        Assert.NotNull(result.DetailUrl);
    }

    [Fact]
    public async Task Image_upload_validates_type_and_bytes_then_serves_publicly()
    {
        var api = _factory.ApiClient(_ws.ApiKey);

        var bad = new ByteArrayContent("<html>"u8.ToArray());
        bad.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var badRes = await api.PostAsync("/api/v1/images", bad);
        Assert.Equal(HttpStatusCode.BadRequest, badRes.StatusCode);
        Assert.Equal(ErrorCodes.Validation, (await badRes.ReadJson<ApiError>()).Code);

        var text = new ByteArrayContent(TinyPng);
        text.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/v1/images", text)).StatusCode);

        var good = new ByteArrayContent(TinyPng);
        good.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var res = await api.PostAsync("/api/v1/images", good);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var upload = await res.ReadJson<ImageUploadResult>();
        Assert.EndsWith(".png", upload.Id);
        Assert.Equal(TinyPng.Length, upload.Bytes);

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(new Uri(upload.Url).PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/i/../appsettings.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/i/nope.png")).StatusCode);

        // subscribe-only keys may not upload
        var sub = _factory.ApiClient(_ws.SubscribeOnlyKey);
        var denied = new ByteArrayContent(TinyPng);
        denied.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        Assert.Equal(HttpStatusCode.Forbidden, (await sub.PostAsync("/api/v1/images", denied)).StatusCode);

        // and the URL is accepted as a publish image
        var pub = await api.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["ci"], "With image", ImageUrl: upload.Url));
        Assert.Equal(HttpStatusCode.Accepted, pub.StatusCode);
        Assert.Equal(upload.Url, (await pub.ReadJson<PublishResult>()).ImageUrl);
    }

    [Fact]
    public async Task Publish_rejects_non_http_image_url_and_oversized_markdown()
    {
        var api = _factory.ApiClient(_ws.ApiKey);
        var res = await api.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["ci"], "x", ImageUrl: "file:///etc/passwd", Markdown: new string('m', Limits.MarkdownMax + 1)));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var err = await res.ReadJson<ApiError>();
        Assert.Contains("imageUrl", err.Details!.Keys);
        Assert.Contains("markdown", err.Details!.Keys);
    }

    [Fact]
    public void Markdown_renders_safely()
    {
        var html = MarkdownRenderer.ToHtml("Hello **bold** <script>alert(1)</script> [x](javascript:alert(2)) [ok](https://example.com)\n\n- [ ] task");
        Assert.Contains("<strong>bold</strong>", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("href=\"https://example.com\"", html);
        Assert.Contains("task-list-item", html);

        Assert.Equal("graph LR\nA-->B", MarkdownRenderer.FirstMermaidBlock("intro\n\n```mermaid\ngraph LR\nA-->B\n```\n\nafter"));
        Assert.Null(MarkdownRenderer.FirstMermaidBlock("```js\nlet x\n```"));
        Assert.Equal("First paragraph here.", MarkdownRenderer.Summary("# Title\n\n```mermaid\ngraph LR\n```\n\nFirst *paragraph* here.\n\nSecond.", 200));
    }

    [Fact]
    public async Task Detail_page_requires_login_and_membership_and_renders_markdown()
    {
        var api = _factory.ApiClient(_ws.ApiKey);
        var res = await api.PostAsJsonAsync("/api/v1/notifications", new PublishRequest(["ci"], "Page test", Markdown: "Some **strong** text\n\n```mermaid\ngraph LR\nA-->B\n```"));
        var result = await res.ReadJson<PublishResult>();
        var path = new Uri(result.DetailUrl!).PathAndQuery;

        var anon = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var redirect = await anon.GetAsync(path);
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Contains("/login", redirect.Headers.Location!.ToString());

        var browser = await _factory.LoginAsync(_ws);
        var page = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("<strong>strong</strong>", html);
        Assert.Contains("language-mermaid", html);
        Assert.Contains("mermaid.min.js", html);

        var other = await _factory.CreateWorkspaceAsync("Other");
        var stranger = await _factory.LoginAsync(other);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(path)).StatusCode);
    }
}
