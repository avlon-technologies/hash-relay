using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HashRelay.Contracts;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Services.Push;

/// <summary>Configuration section <c>HashRelay:Push:Fcm</c>. Either the JSON itself (as a secret) or a path to the key file.</summary>
public sealed class FcmOptions
{
    /// <summary>Contents of a Firebase service-account key (type "service_account") with the Firebase Cloud Messaging API enabled. Env: HashRelay__Push__Fcm__ServiceAccountJson.</summary>
    public string ServiceAccountJson { get; set; } = "";
    /// <summary>Path to the service-account key file, used when <see cref="ServiceAccountJson"/> is empty.</summary>
    public string ServiceAccountPath { get; set; } = "";
    /// <summary>FCM HTTP v1 base URL; overridable for tests.</summary>
    public string MessagingBaseUrl { get; set; } = "https://fcm.googleapis.com";
    /// <summary>Android notification channel the app creates; must match the client.</summary>
    public string AndroidChannelId { get; set; } = "hashrelay";
}

/// <summary>
/// Firebase Cloud Messaging HTTP v1 sender. Authenticates with a service-account JWT exchanged for a short-lived OAuth2
/// access token (cached until shortly before expiry). Sends a display notification plus a data payload so the OS shows
/// the message when the app is backgrounded or killed, and the app can act on it when opened. iOS devices are reached
/// through Firebase's APNs relay; HashRelay never talks to APNs itself.
/// </summary>
public sealed class FcmPushSender : IPushSender
{
    public const string HttpClientName = "fcm";
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _clock;
    private readonly ILogger<FcmPushSender> _log;
    private readonly FcmOptions _options;
    private readonly ServiceAccountKey? _key;
    private readonly RSA? _rsa;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheLock = new();
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpires;

    public FcmPushSender(IOptions<FcmOptions> options, IHttpClientFactory http, TimeProvider clock, ILogger<FcmPushSender> log)
    {
        _http = http; _clock = clock; _log = log; _options = options.Value;
        try
        {
            var json = _options.ServiceAccountJson;
            if (string.IsNullOrWhiteSpace(json) && !string.IsNullOrWhiteSpace(_options.ServiceAccountPath)) json = File.ReadAllText(_options.ServiceAccountPath);
            if (string.IsNullOrWhiteSpace(json)) return;
            var key = JsonSerializer.Deserialize<ServiceAccountKey>(json);
            if (key is null || key.Type != "service_account" || string.IsNullOrEmpty(key.ProjectId) || string.IsNullOrEmpty(key.ClientEmail) || string.IsNullOrEmpty(key.PrivateKey))
            {
                _log.LogError("FCM service account is not a valid service_account key (needs project_id, client_email, private_key); push disabled");
                return;
            }
            var rsa = RSA.Create();
            rsa.ImportFromPem(key.PrivateKey);
            _rsa = rsa; _key = key;
            _log.LogInformation("FCM push enabled for Firebase project {Project}", key.ProjectId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FCM service account could not be loaded; push disabled");
        }
    }

    public bool Enabled => _key is not null;
    public string Provider => PushProviders.Fcm;

    public async Task<PushResult> SendAsync(PushMessage message, CancellationToken ct)
    {
        if (_key is null) return new PushResult(PushOutcome.Failed, null, "FCM is not configured.");
        string accessToken;
        try { accessToken = await AccessTokenAsync(ct); }
        catch (Exception ex)
        {
            _log.LogError(ex, "FCM access token could not be obtained");
            return new PushResult(PushOutcome.Failed, null, "FCM authentication failed: " + ex.Message);
        }

        var client = _http.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.MessagingBaseUrl.TrimEnd('/')}/v1/projects/{_key.ProjectId}/messages:send");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(JsonSerializer.Serialize(new { message = Build(message) }), Encoding.UTF8, "application/json");

        HttpResponseMessage res;
        try { res = await client.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return new PushResult(PushOutcome.Failed, null, ex.Message); }
        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
            {
                string? name = null;
                try { name = JsonDocument.Parse(body).RootElement.TryGetProperty("name", out var n) ? n.GetString() : null; } catch (JsonException) { }
                return new PushResult(PushOutcome.Accepted, name, null);
            }
            var error = Summarize(body, (int)res.StatusCode);
            if (res.StatusCode == HttpStatusCode.NotFound || body.Contains("UNREGISTERED", StringComparison.Ordinal)
                || (res.StatusCode == HttpStatusCode.BadRequest && body.Contains("registration token", StringComparison.OrdinalIgnoreCase)))
                return new PushResult(PushOutcome.InvalidToken, null, error);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _log.LogError("FCM rejected the service account credential: {Error}", error);
                lock (_cacheLock) { _accessToken = null; }
            }
            return new PushResult(PushOutcome.Failed, null, error);
        }
    }

    private object Build(PushMessage m)
    {
        var high = m.Priority == Priorities.High;
        var notification = new Dictionary<string, string> { ["title"] = m.Title, ["body"] = m.Body };
        if (m.ImageUrl is not null) notification["image"] = m.ImageUrl;
        var aps = new Dictionary<string, object>
        {
            ["sound"] = "default",
            ["thread-id"] = m.Data.TryGetValue("tenantId", out var tid) ? tid : "hashrelay",
            ["mutable-content"] = 1,
        };
        return new
        {
            token = m.Token,
            notification,
            data = m.Data,
            android = new
            {
                priority = high ? "HIGH" : "NORMAL",
                collapse_key = m.CollapseId,
                notification = new { channel_id = _options.AndroidChannelId, tag = m.CollapseId },
            },
            apns = new
            {
                headers = new Dictionary<string, string> { ["apns-priority"] = high ? "10" : "5", ["apns-collapse-id"] = m.CollapseId },
                payload = new { aps },
            },
        };
    }

    private static string Summarize(string body, int status)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                var code = err.TryGetProperty("status", out var s) ? s.GetString() : null;
                return $"{status} {code}: {msg}";
            }
        }
        catch (JsonException) { }
        return $"{status}: {(body.Length > 200 ? body[..200] : body)}";
    }

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        lock (_cacheLock) { if (_accessToken is not null && now < _accessTokenExpires) return _accessToken; }
        await _gate.WaitAsync(ct);
        try
        {
            lock (_cacheLock) { if (_accessToken is not null && now < _accessTokenExpires) return _accessToken; }
            var assertion = SignedJwt(now);
            var client = _http.CreateClient(HttpClientName);
            using var res = await client.PostAsync(_key!.TokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            }), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"token endpoint returned {(int)res.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
            var root = JsonDocument.Parse(body).RootElement;
            var token = root.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("token endpoint returned no access_token");
            var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            lock (_cacheLock)
            {
                _accessToken = token;
                _accessTokenExpires = now.AddSeconds(Math.Max(60, expiresIn - 60));
            }
            return token;
        }
        finally { _gate.Release(); }
    }

    /// <summary>RS256 JWT for the OAuth2 service-account flow (RFC 7523), signed with the key's private key.</summary>
    private string SignedJwt(DateTimeOffset now)
    {
        var iat = now.ToUnixTimeSeconds();
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = _key!.ClientEmail, scope = Scope, aud = _key.TokenUri, iat, exp = iat + 3600,
        }));
        var input = header + "." + claims;
        var signature = _rsa!.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return input + "." + Base64Url.EncodeToString(signature);
    }

    private sealed record ServiceAccountKey(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("project_id")] string ProjectId,
        [property: JsonPropertyName("client_email")] string ClientEmail,
        [property: JsonPropertyName("private_key")] string PrivateKey,
        [property: JsonPropertyName("token_uri")] string TokenUri = "https://oauth2.googleapis.com/token");
}
