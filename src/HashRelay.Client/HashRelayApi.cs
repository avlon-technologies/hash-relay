using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HashRelay.Contracts;

namespace HashRelay.Client;

public sealed class HashRelayApiException(HttpStatusCode status, ApiError? error, string raw)
    : Exception(error?.Message ?? $"HashRelay API returned {(int)status} {status}.")
{
    public HttpStatusCode Status { get; } = status;
    public ApiError? Error { get; } = error;
    public string Raw { get; } = raw;
    public bool IsUnauthorized => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

/// <summary>Thin typed wrapper over the HashRelay HTTP API.</summary>
public sealed class HashRelayApi : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public HashRelayApi(Uri server, string? apiKey, HttpClient? http = null, string? userAgent = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        _http.BaseAddress = server;
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? $"hashrelay-client/{ClientVersion.Current}");
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public Task<HealthResponse> HealthAsync(CancellationToken ct = default)
        => GetAsync<HealthResponse>("healthz", ct);

    public Task<MeResponse> MeAsync(CancellationToken ct = default)
        => GetAsync<MeResponse>("api/v1/me", ct);

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync("api/v1/notifications", request, Json, ct);
        return await ReadAsync<PublishResult>(res, ct);
    }

    /// <summary>Uploads an image (png/jpeg/gif/webp, ≤ 3 MB) and returns its public URL for <see cref="PublishRequest.ImageUrl"/>.</summary>
    public async Task<ImageUploadResult> UploadImageAsync(ReadOnlyMemory<byte> bytes, string contentType, CancellationToken ct = default)
    {
        using var content = new ReadOnlyMemoryContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var res = await _http.PostAsync("api/v1/images", content, ct);
        return await ReadAsync<ImageUploadResult>(res, ct);
    }

    public static string? ImageContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => null,
    };

    public Task<IReadOnlyList<NotificationSummary>> RecentNotificationsAsync(int limit = 20, CancellationToken ct = default)
        => GetAsync<IReadOnlyList<NotificationSummary>>($"api/v1/notifications?limit={limit}", ct);

    public Task<IReadOnlyList<EndpointInfo>> EndpointsAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<EndpointInfo>>("api/v1/endpoints", ct);

    public async Task<EndpointInfo> RegisterEndpointAsync(string endpointId, RegisterEndpointRequest request, CancellationToken ct = default)
    {
        using var res = await _http.PutAsJsonAsync($"api/v1/endpoints/{Uri.EscapeDataString(endpointId)}", request, Json, ct);
        return await ReadAsync<EndpointInfo>(res, ct);
    }

    public Task<SubscriptionsResponse> GetSubscriptionsAsync(string endpointId, CancellationToken ct = default)
        => GetAsync<SubscriptionsResponse>($"api/v1/endpoints/{Uri.EscapeDataString(endpointId)}/subscriptions", ct);

    public async Task<SubscriptionsResponse> ReplaceSubscriptionsAsync(string endpointId, IEnumerable<string> tags, CancellationToken ct = default)
    {
        using var res = await _http.PutAsJsonAsync($"api/v1/endpoints/{Uri.EscapeDataString(endpointId)}/subscriptions", new SubscriptionsRequest(tags.ToList()), Json, ct);
        return await ReadAsync<SubscriptionsResponse>(res, ct);
    }

    public async Task<SubscriptionsResponse> AddSubscriptionsAsync(string endpointId, IEnumerable<string> tags, CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync($"api/v1/endpoints/{Uri.EscapeDataString(endpointId)}/subscriptions", new SubscriptionsRequest(tags.ToList()), Json, ct);
        return await ReadAsync<SubscriptionsResponse>(res, ct);
    }

    public async Task<SubscriptionsResponse> RemoveSubscriptionsAsync(string endpointId, IEnumerable<string> tags, CancellationToken ct = default)
    {
        var query = string.Join("&", tags.Select(t => "tags=" + Uri.EscapeDataString(t)));
        using var res = await _http.DeleteAsync($"api/v1/endpoints/{Uri.EscapeDataString(endpointId)}/subscriptions?{query}", ct);
        return await ReadAsync<SubscriptionsResponse>(res, ct);
    }

    // ------------------------------------------------------------------ one-click approvals (device token + decision grant)

    /// <summary>Starts enrolment for one-click approvals; the person then grants it in the browser at the returned URI.</summary>
    public async Task<DecisionGrantStart> BeginDecisionGrantAsync(CancellationToken ct = default)
    {
        using var res = await _http.PostAsync("api/v1/decision-grants", null, ct);
        return await ReadAsync<DecisionGrantStart>(res, ct);
    }

    public async Task<DecisionGrantPoll> PollDecisionGrantAsync(string pollCode, CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync("api/v1/decision-grants/poll", new DecisionGrantPollRequest(pollCode), Json, ct);
        return await ReadAsync<DecisionGrantPoll>(res, ct);
    }

    public async Task RevokeDecisionGrantAsync(string grantSecret, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, "api/v1/decision-grants/current");
        req.Headers.Add(DecisionGrants.Header, grantSecret);
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) await ReadAsync<object>(res, ct);
    }

    /// <summary>Submits the grant holder's decision. Throws <see cref="HashRelayApiException"/> on any refusal; success is only what the server returns.</summary>
    public async Task<ApprovalResource> DecideOnDeviceAsync(Guid approvalId, string decision, Guid deliveryId, string grantSecret, string idempotencyKey, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"api/v1/approvals/{approvalId}/device-decision")
        {
            Content = JsonContent.Create(new DeviceDecisionRequest(decision, deliveryId), options: Json),
        };
        req.Headers.Add(DecisionGrants.Header, grantSecret);
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        using var res = await _http.SendAsync(req, ct);
        return await ReadAsync<ApprovalResource>(res, ct);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(path, ct);
        return await ReadAsync<T>(res, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        var raw = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            ApiError? err = null;
            try { err = JsonSerializer.Deserialize<ApiError>(raw, Json); } catch (JsonException) { }
            throw new HashRelayApiException(res.StatusCode, err, raw);
        }
        return JsonSerializer.Deserialize<T>(raw, Json) ?? throw new HashRelayApiException(res.StatusCode, null, raw);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public static class ClientVersion
{
    public static string Current { get; } =
        typeof(ClientVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
