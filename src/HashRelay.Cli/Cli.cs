using System.Diagnostics;
using System.Net.Http.Json;
using System.Net;
using System.Reflection;
using HashRelay.Client;
using HashRelay.Contracts;

namespace HashRelay.Cli;

public static class Cli
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string DefaultServer { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "DefaultServer")?.Value
        ?? RelayConfig.DefaultServer;

    private static string Platform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "unknown";
    private const string TestTag = "test";

    public static Task<int> RunAsync(string command, Args a, CancellationToken ct) => command switch
    {
        "login" => LoginAsync(a, ct),
        "logout" => Task.FromResult(Logout()),
        "configure" or "config" => ConfigureAsync(a, ct),
        "status" => StatusAsync(a, ct),
        "subscribe" or "sub" => SubscriptionsAsync(a, "add", ct),
        "unsubscribe" or "unsub" => SubscriptionsAsync(a, "remove", ct),
        "subscriptions" or "subs" => SubscriptionsAsync(a, "list", ct),
        "publish" or "pub" or "send" => PublishAsync(a, ct),
        "listen" => ListenAsync(a, ct),
        "test" => TestAsync(a, ct),
        "agent" => AgentAsync(a, ct),
        "open" => Task.FromResult(Open(a)),
        _ => Task.FromResult(Terminal.Fail(ExitCodes.InvalidInput, $"Unknown command '{command}'.", "Run `hashrelay --help` for the list of commands.")),
    };

    // ---------------------------------------------------------------- helpers

    private static RelayConfig LoadConfig(Args a)
    {
        var cfg = ConfigStore.Load();
        if (!ConfigStore.Exists) cfg.Server = DefaultServer;
        if (a.Get("server") is { } s) cfg.Server = NormalizeServer(s);
        if (a.Get("key") is { } k) cfg.ApiKey = k;
        return cfg;
    }

    private static string NormalizeServer(string s)
    {
        s = s.Trim();
        if (!s.Contains("://")) s = (s.StartsWith("localhost") || s.StartsWith("127.") ? "http://" : "https://") + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw new HashRelayApiException(HttpStatusCode.BadRequest, new ApiError(ErrorCodes.Validation, $"'{s}' is not a valid server URL."), s);
        return u.GetLeftPart(UriPartial.Authority);
    }

    private static HashRelayApi Api(RelayConfig cfg) => new(cfg.ServerUri, cfg.ApiKey, userAgent: $"hashrelay-cli/{Version} ({Platform})");

    private static RelayConfig RequireLogin(Args a)
    {
        var cfg = LoadConfig(a);
        if (!cfg.IsLoggedIn)
            throw new HashRelayApiException(HttpStatusCode.Unauthorized, new ApiError(ErrorCodes.Unauthenticated, "This device is not paired with a workspace."), "");
        return cfg;
    }

    public static int MapApiError(HashRelayApiException ex)
    {
        var detail = ex.Error?.Details is { Count: > 0 } d ? string.Join(" ", d.SelectMany(kv => kv.Value)) : null;
        return ex.Status switch
        {
            HttpStatusCode.Unauthorized => Terminal.Fail(ExitCodes.Authentication, ex.Message, "Run `hashrelay login` to pair this device, or pass --key.", ex.Error),
            HttpStatusCode.Forbidden => Terminal.Fail(ExitCodes.Authentication, ex.Message, "Your device token may lack this permission. Pair again with `hashrelay login` and allow publishing, or use an API key with --key.", ex.Error),
            HttpStatusCode.BadRequest => Terminal.Fail(ExitCodes.InvalidInput, detail ?? ex.Message, null, ex.Error),
            HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests => Terminal.Fail(ExitCodes.Rejected, ex.Message, null, ex.Error),
            >= HttpStatusCode.InternalServerError => Terminal.Fail(ExitCodes.ServerUnavailable, $"The server failed ({(int)ex.Status}). {ex.Message}", null, ex.Error),
            _ => Terminal.Fail(ExitCodes.Unexpected, ex.Message, null, ex.Error),
        };
    }

    private static List<string> ParseTags(Args a, out List<string> rest)
    {
        rest = [];
        var tags = new List<string>();
        foreach (var p in a.Positionals.Skip(1))
        {
            if (Tags.LooksLikeTag(p)) tags.Add(p); else rest.Add(p);
        }
        tags.AddRange(a.All("tag", "t").SelectMany(t => t.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)));
        return tags;
    }

    private static void TryOpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* headless; the URL is printed anyway */ }
    }

    // ---------------------------------------------------------------- commands

    private static async Task<int> LoginAsync(Args a, CancellationToken ct)
    {
        var cfg = LoadConfig(a);
        if (a.Get("key") is { } key)
        {
            using var api = Api(cfg);
            var me = await api.MeAsync(ct);
            cfg.ApiKey = key;
            Remember(cfg, me.Tenant, me.Credential);
            Terminal.Ok($"Signed in to {Terminal.Bold(me.Tenant.Name)} with {me.Credential.Kind} credential {Terminal.Dim(me.Credential.Hint)} ({string.Join(", ", me.Credential.Scopes)}).");
            Terminal.Emit(new { ok = true, tenant = me.Tenant, credential = me.Credential, server = cfg.Server });
            return ExitCodes.Ok;
        }

        using var anon = new HashRelayApi(cfg.ServerUri, null, userAgent: $"hashrelay-cli/{Version}");
        // The device-code endpoints live on HashRelayApi's HttpClient, so use a small inline client here.
        var http = new HttpClient { BaseAddress = cfg.ServerUri, Timeout = TimeSpan.FromSeconds(20) };
        var codeRes = await http.PostAsJsonAsync("api/v1/device/code", new DeviceCodeRequest(cfg.EndpointId, cfg.DeviceName, Platform, Version, "cli"), ct);
        if (!codeRes.IsSuccessStatusCode) throw new HashRelayApiException(codeRes.StatusCode, await TryReadError(codeRes), "");
        var code = (await codeRes.Content.ReadFromJsonAsync<DeviceCodeResponse>(ct))!;

        Terminal.Line();
        Terminal.Line($"  Pair {Terminal.Bold(cfg.DeviceName)} with your workspace.");
        Terminal.Line($"  Open {Terminal.Cyan(code.VerificationUrlComplete)}");
        Terminal.Line($"  and confirm the code {Terminal.Bold(Terminal.Magenta(code.UserCode))}");
        Terminal.Line();
        if (!a.Has("no-browser")) TryOpenBrowser(code.VerificationUrlComplete);
        Terminal.Info("Waiting for approval… (Ctrl+C to cancel)");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(code.ExpiresInSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, code.IntervalSeconds)), ct);
            var pollRes = await http.PostAsJsonAsync("api/v1/device/token", new DeviceTokenRequest(code.DeviceCode), ct);
            if (!pollRes.IsSuccessStatusCode) continue;
            var poll = (await pollRes.Content.ReadFromJsonAsync<DeviceTokenResponse>(ct))!;
            switch (poll.Status)
            {
                case "pending": continue;
                case "denied": return Terminal.Fail(ExitCodes.Authentication, "The pairing request was denied in the dashboard.");
                case "expired": return Terminal.Fail(ExitCodes.Authentication, "The pairing code expired before it was approved.", "Run `hashrelay login` again.");
                case "approved":
                    cfg.ApiKey = poll.Token;
                    Remember(cfg, poll.Tenant!, poll.Credential!);
                    Terminal.Ok($"Paired with {Terminal.Bold(poll.Tenant!.Name)}. This device may: {string.Join(", ", poll.Credential!.Scopes)}.");
                    Terminal.Info($"Next: {Terminal.Cyan("hashrelay subscribe #builds")} then {Terminal.Cyan("hashrelay test")}");
                    Terminal.Emit(new { ok = true, tenant = poll.Tenant, credential = poll.Credential, server = cfg.Server, endpointId = cfg.EndpointId });
                    return ExitCodes.Ok;
            }
        }
        return Terminal.Fail(ExitCodes.Authentication, "The pairing code expired before it was approved.", "Run `hashrelay login` again.");
    }

    private static async Task<ApiError?> TryReadError(HttpResponseMessage res)
    {
        try { return await res.Content.ReadFromJsonAsync<ApiError>(); } catch { return null; }
    }

    private static void Remember(RelayConfig cfg, TenantInfo tenant, CredentialInfo cred)
    {
        cfg.TenantName = tenant.Name;
        cfg.TenantSlug = tenant.Slug;
        cfg.KeyLabel = cred.Label;
        cfg.LoggedInAt = DateTimeOffset.UtcNow;
        ConfigStore.Save(cfg);
    }

    private static int Logout()
    {
        var cfg = ConfigStore.Load();
        if (!cfg.IsLoggedIn) { Terminal.Info("Not signed in."); return ExitCodes.Ok; }
        cfg.ApiKey = null; cfg.TenantName = null; cfg.TenantSlug = null; cfg.KeyLabel = null; cfg.LoggedInAt = null;
        ConfigStore.Save(cfg);
        Terminal.Ok("Signed out. The device token remains listed in the dashboard until you revoke it.");
        Terminal.Emit(new { ok = true });
        return ExitCodes.Ok;
    }

    private static async Task<int> ConfigureAsync(Args a, CancellationToken ct)
    {
        var cfg = ConfigStore.Load();
        if (!ConfigStore.Exists) cfg.Server = DefaultServer;
        var changed = false;
        if (a.Get("server") is { } s)
        {
            var server = NormalizeServer(s);
            if (!string.Equals(server, cfg.Server, StringComparison.OrdinalIgnoreCase) && cfg.IsLoggedIn)
            {
                Terminal.Warn("Changing the server clears the stored credential; run `hashrelay login` afterwards.");
                cfg.ApiKey = null; cfg.TenantName = null; cfg.TenantSlug = null; cfg.KeyLabel = null;
            }
            cfg.Server = server; changed = true;
        }
        if (a.Get("name") is { } n && n.Trim().Length > 0) { cfg.DeviceName = n.Trim(); changed = true; }
        if (!changed && !ConfigStore.Exists) changed = true;
        if (changed) ConfigStore.Save(cfg);

        using var api = Api(cfg);
        string health;
        try { var h = await api.HealthAsync(ct); health = Terminal.Green($"reachable (v{h.Version})"); }
        catch (Exception ex) { health = Terminal.Red($"unreachable: {ex.Message}"); }
        Terminal.KeyValue("server", $"{cfg.Server}  {health}");
        Terminal.KeyValue("device", $"{cfg.DeviceName} {Terminal.Dim(cfg.EndpointId)}");
        Terminal.KeyValue("config", ConfigStore.FilePath);
        Terminal.Emit(new { ok = true, server = cfg.Server, deviceName = cfg.DeviceName, endpointId = cfg.EndpointId, config = ConfigStore.FilePath });
        return ExitCodes.Ok;
    }

    private static async Task<int> StatusAsync(Args a, CancellationToken ct)
    {
        var cfg = LoadConfig(a);
        using var api = Api(cfg);
        object? health = null, me = null, endpoint = null;
        string healthText, loginText, deviceText;
        try { var h = await api.HealthAsync(ct); health = h; healthText = Terminal.Green($"online · v{h.Version}"); }
        catch (Exception ex) { healthText = Terminal.Red("unreachable") + Terminal.Dim($" ({ex.Message})"); }

        if (!cfg.IsLoggedIn) { loginText = Terminal.Yellow("not paired") + Terminal.Dim(" — run `hashrelay login`"); deviceText = Terminal.Dim("—"); }
        else
        {
            try
            {
                var m = await api.MeAsync(ct); me = m;
                loginText = $"{Terminal.Bold(m.Tenant.Name)} {Terminal.Dim($"({m.Tenant.Slug})")} · {m.Credential.Kind} {Terminal.Dim(m.Credential.Hint)} · {string.Join(", ", m.Credential.Scopes)}";
                try
                {
                    var ep = await api.GetSubscriptionsAsync(cfg.EndpointId, ct);
                    var info = (await api.EndpointsAsync(ct)).FirstOrDefault(e => e.EndpointId == cfg.EndpointId);
                    endpoint = info;
                    var online = info?.Online == true ? Terminal.Green("agent connected") : Terminal.Yellow("agent offline");
                    deviceText = $"{cfg.DeviceName} · {online} · {(ep.Tags.Count == 0 ? Terminal.Dim("no subscriptions") : string.Join(' ', ep.Tags.Select(Terminal.Tag)))}";
                }
                catch (HashRelayApiException ex) when (ex.Status == HttpStatusCode.NotFound) { deviceText = $"{cfg.DeviceName} · {Terminal.Yellow("not registered yet")} — start the agent or run `hashrelay subscribe`"; }
            }
            catch (HashRelayApiException ex) when (ex.IsUnauthorized) { loginText = Terminal.Red("credential rejected") + Terminal.Dim($" — {ex.Message}"); deviceText = Terminal.Dim("—"); }
        }

        var agentRunning = OperatingSystem.IsWindows() && AgentProcess() is not null;
        Terminal.Line(Terminal.Bold("HashRelay status"));
        Terminal.KeyValue("server", $"{cfg.Server} · {healthText}");
        Terminal.KeyValue("workspace", loginText);
        Terminal.KeyValue("device", deviceText);
        if (OperatingSystem.IsWindows()) Terminal.KeyValue("agent", agentRunning ? Terminal.Green("running") : Terminal.Yellow("not running") + Terminal.Dim(" — `hashrelay agent start`"));
        Terminal.KeyValue("endpoint", Terminal.Dim(cfg.EndpointId));
        Terminal.KeyValue("config", Terminal.Dim(ConfigStore.FilePath));
        Terminal.Emit(new { ok = true, server = cfg.Server, health, loggedIn = cfg.IsLoggedIn, me, endpoint, agentRunning, endpointId = cfg.EndpointId, config = ConfigStore.FilePath });
        return ExitCodes.Ok;
    }

    private static async Task<int> SubscriptionsAsync(Args a, string mode, CancellationToken ct)
    {
        var cfg = RequireLogin(a);
        using var api = Api(cfg);
        SubscriptionsResponse result;
        if (mode == "list")
        {
            try { result = await api.GetSubscriptionsAsync(cfg.EndpointId, ct); }
            catch (HashRelayApiException ex) when (ex.Status == HttpStatusCode.NotFound) { result = new SubscriptionsResponse(cfg.EndpointId, []); }
        }
        else
        {
            var tags = ParseTags(a, out var rest);
            tags.AddRange(rest); // allow bare words too: `subscribe builds`
            if (tags.Count == 0) return Terminal.Fail(ExitCodes.InvalidInput, "Give at least one hashtag.", $"Example: hashrelay {mode switch { "add" => "subscribe", _ => "unsubscribe" }} #builds");
            try { foreach (var t in tags) Tags.Normalize(t); }
            catch (ArgumentException ex) { return Terminal.Fail(ExitCodes.InvalidInput, ex.Message); }
            await EnsureRegisteredAsync(api, cfg, ct);
            result = mode == "add" ? await api.AddSubscriptionsAsync(cfg.EndpointId, tags, ct) : await api.RemoveSubscriptionsAsync(cfg.EndpointId, tags, ct);
            var verb = mode == "add" ? "Subscribed" : "Unsubscribed";
            Terminal.Ok($"{verb} {Terminal.Bold(cfg.DeviceName)} {(mode == "add" ? "to" : "from")} {string.Join(' ', tags.Select(t => Terminal.Tag(Tags.Normalize(t))))}");
        }
        Terminal.Line(result.Tags.Count == 0 ? Terminal.Dim("  (no subscriptions)") : "  " + string.Join(' ', result.Tags.Select(Terminal.Tag)));
        Terminal.Emit(new { ok = true, endpointId = result.EndpointId, tags = result.Tags });
        return ExitCodes.Ok;
    }

    private static async Task EnsureRegisteredAsync(HashRelayApi api, RelayConfig cfg, CancellationToken ct)
    {
        try { await api.RegisterEndpointAsync(cfg.EndpointId, new RegisterEndpointRequest(cfg.EndpointId, Platform, cfg.DeviceName, Version), ct); }
        catch (HashRelayApiException ex) when (ex.Status == HttpStatusCode.Forbidden) { /* subscribe-scope missing; let the real call report */ }
    }

    private static async Task<int> PublishAsync(Args a, CancellationToken ct)
    {
        if (a.Has("help", "h")) { Help.PrintCommand(Help.Find("publish")!); return ExitCodes.Ok; }
        var cfg = RequireLogin(a);
        var tags = ParseTags(a, out var rest);
        var title = a.Get("title") ?? rest.ElementAtOrDefault(0);
        var body = a.Get("body", "b") ?? rest.ElementAtOrDefault(1);
        if (tags.Count == 0) return Terminal.Fail(ExitCodes.InvalidInput, "Give at least one hashtag.", "Example: hashrelay publish #builds \"Build complete\" \"212 tests passed\"");
        if (string.IsNullOrWhiteSpace(title)) return Terminal.Fail(ExitCodes.InvalidInput, "A title is required.", "Example: hashrelay publish #builds \"Build complete\"");
        Guid? id = null;
        if (a.Get("id") is { } rawId)
        {
            if (!Guid.TryParse(rawId, out var g)) return Terminal.Fail(ExitCodes.InvalidInput, "--id must be a UUID.");
            id = g;
        }
        string? markdown, diagram;
        try
        {
            markdown = a.Get("markdown", "m") ?? ReadFileOption(a.Get("markdown-file"));
            diagram = a.Get("diagram", "d") ?? ReadFileOption(a.Get("diagram-file"));
        }
        catch (IOException ex) { return Terminal.Fail(ExitCodes.InvalidInput, ex.Message); }
        if (markdown is not null && markdown.Length > Limits.MarkdownMax) return Terminal.Fail(ExitCodes.InvalidInput, $"Markdown is longer than {Limits.MarkdownMax} characters.");
        if (diagram is not null && diagram.Length > Limits.DiagramMax) return Terminal.Fail(ExitCodes.InvalidInput, $"Diagram is longer than {Limits.DiagramMax} characters.");

        using var api = Api(cfg);
        var sw = Stopwatch.StartNew();
        var imageUrl = a.Get("image");
        if (imageUrl is not null && !imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var path = imageUrl;
            if (!File.Exists(path)) return Terminal.Fail(ExitCodes.InvalidInput, $"Image file not found: {path}");
            var contentType = HashRelayApi.ImageContentTypeFor(path);
            if (contentType is null) return Terminal.Fail(ExitCodes.InvalidInput, "Image must be a .png, .jpg, .gif or .webp file.");
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.Length > Limits.ImageMaxBytes) return Terminal.Fail(ExitCodes.InvalidInput, $"Image is larger than {Limits.ImageMaxBytes / 1024 / 1024} MB.");
            var upload = await api.UploadImageAsync(bytes, contentType, ct);
            imageUrl = upload.Url;
            Terminal.Info($"uploaded {Path.GetFileName(path)} ({bytes.Length / 1024} KB) → {Terminal.Dim(upload.Url)}");
        }
        var result = await api.PublishAsync(new PublishRequest(tags, title, body, a.Get("url"), a.Get("priority", "p"), id, markdown, imageUrl, diagram), ct);
        sw.Stop();
        var delivery = result.MatchedEndpoints == 0
            ? Terminal.Yellow("no subscribed devices")
            : $"{result.MatchedEndpoints} device{(result.MatchedEndpoints == 1 ? "" : "s")} matched · {Terminal.Green($"{result.DeliveredLive} live")}{(result.Queued > 0 ? $" · {Terminal.Yellow($"{result.Queued} queued")}" : "")}";
        Terminal.Ok($"{(result.Duplicate ? "Already accepted" : "Accepted")} {Terminal.Dim(result.NotificationId.ToString())} · {delivery} {Terminal.Dim($"({sw.ElapsedMilliseconds} ms)")}");
        if (result.MatchedEndpoints == 0) Terminal.Info($"Subscribe a device first: {Terminal.Cyan($"hashrelay subscribe {Tags.DisplayAll(result.Tags)}")}");
        if ((markdown is not null || diagram is not null) && result.DetailUrl is not null) Terminal.Info($"page: {Terminal.Dim(result.DetailUrl)}");
        if (result.ImageUrl is null && (diagram is not null || (markdown?.Contains("```mermaid", StringComparison.OrdinalIgnoreCase) ?? false)))
            Terminal.Warn("The diagram could not be rendered to an image (check its Mermaid syntax); the toast will show text only and the page will show the error.");
        Terminal.Emit(new { ok = true, result });
        return ExitCodes.Ok;
    }

    /// <summary>Reads a --*-file option: a path, or '-' for stdin. Returns null when the option is absent.</summary>
    private static string? ReadFileOption(string? path)
    {
        if (path is null) return null;
        if (path == "-") return Console.In.ReadToEnd();
        if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}");
        return File.ReadAllText(path);
    }

    private static async Task<int> ListenAsync(Args a, CancellationToken ct)
    {
        var cfg = RequireLogin(a);
        var tags = ParseTags(a, out var rest); tags.AddRange(rest);
        await using var listener = new RelayListener(cfg, Platform, Version);
        var done = new TaskCompletionSource();
        listener.StateChanged += (s, ex) =>
        {
            var text = s switch
            {
                ListenerState.Connected => Terminal.Green("connected"),
                ListenerState.Connecting => Terminal.Dim("connecting…"),
                ListenerState.Reconnecting => Terminal.Yellow("reconnecting…") + (ex is null ? "" : Terminal.Dim($" ({ex.Message})")),
                _ => Terminal.Red("disconnected") + (ex is null ? "" : Terminal.Dim($" ({ex.Message})")),
            };
            if (!Terminal.Json) Console.Error.WriteLine($"{Terminal.Dim(DateTime.Now.ToString("HH:mm:ss"))} {text}");
        };
        listener.Registered += async hello =>
        {
            if (tags.Count > 0)
            {
                try { var now = await listener.SubscribeAsync(tags, ct); Terminal.Info($"subscriptions: {string.Join(' ', now.Select(Terminal.Tag))}"); }
                catch (Exception ex) { Terminal.Warn($"subscribe failed: {ex.Message}"); }
            }
            else Terminal.Info($"{Terminal.Bold(hello.Tenant.Name)} · listening as {hello.Name} on {(hello.Subscriptions.Count == 0 ? Terminal.Dim("(no subscriptions)") : string.Join(' ', hello.Subscriptions.Select(Terminal.Tag)))}");
        };
        listener.SubscriptionsChanged += t => Terminal.Info($"subscriptions changed: {string.Join(' ', t.Select(Terminal.Tag))}");
        listener.NotificationReceived += m =>
        {
            if (Terminal.Json) Terminal.Emit(m);
            else
            {
                var from = m.Publisher?.IsKnown == true ? Terminal.Dim($" — {m.Publisher.Name}") : "";
                Console.WriteLine($"{Terminal.Dim(m.PublishedAtUtc.ToLocalTime().ToString("HH:mm:ss"))} {string.Join(' ', m.Tags.Select(Terminal.Tag))} {Terminal.Bold(m.Title)}{from}{(m.Replayed ? Terminal.Dim(" (replayed)") : "")}");
                if (!string.IsNullOrEmpty(m.Body)) Console.WriteLine($"         {m.Body}");
                if (!string.IsNullOrEmpty(m.Url)) Console.WriteLine($"         {Terminal.Dim(m.Url)}");
                if (m.Rich && !string.IsNullOrEmpty(m.DetailUrl)) Console.WriteLine($"         {Terminal.Dim("page: " + m.DetailUrl)}");
            }
            _ = listener.AckAsync(m.DeliveryId);
        };
        await listener.StartAsync(ct);
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        return ExitCodes.Ok;
    }

    private static async Task<int> TestAsync(Args a, CancellationToken ct)
    {
        var cfg = RequireLogin(a);
        using var api = Api(cfg);
        var me = await api.MeAsync(ct);
        Terminal.Info($"Workspace {Terminal.Bold(me.Tenant.Name)} · server v{me.ServerVersion}");

        if (me.Credential.Scopes.Contains(Scopes.Subscribe))
        {
            await EnsureRegisteredAsync(api, cfg, ct);
            var subs = await api.GetSubscriptionsAsync(cfg.EndpointId, ct);
            if (!subs.Tags.Contains(TestTag))
            {
                await api.AddSubscriptionsAsync(cfg.EndpointId, [TestTag], ct);
                Terminal.Info($"Subscribed this device to {Terminal.Tag(TestTag)}");
            }
        }
        if (!me.Credential.Scopes.Contains(Scopes.Publish))
            return Terminal.Fail(ExitCodes.Authentication, "This device token cannot publish.", "Pair again with `hashrelay login` and allow publishing, or pass an API key with --key.");

        var result = await api.PublishAsync(new PublishRequest([TestTag], "HashRelay test", $"If you can see this on {cfg.DeviceName}, notifications are working.", Priority: Priorities.High), ct);
        if (result.MatchedEndpoints == 0)
            return Terminal.Fail(ExitCodes.Rejected, "The test notification matched no devices.", "Start the agent (`hashrelay agent start`) or run `hashrelay subscribe #test` on a paired device.", result);
        if (result.DeliveredLive == 0)
        {
            Terminal.Warn($"Accepted, but no device is connected right now ({result.Queued} queued). It will be shown when the agent connects.");
            if (OperatingSystem.IsWindows()) Terminal.Info($"Start it with {Terminal.Cyan("hashrelay agent start")}");
            Terminal.Emit(new { ok = true, delivered = false, result });
            return ExitCodes.Ok;
        }
        Terminal.Ok($"Test notification delivered live to {result.DeliveredLive} device{(result.DeliveredLive == 1 ? "" : "s")}. Check Windows Notification Center.");
        Terminal.Emit(new { ok = true, delivered = true, result });
        return ExitCodes.Ok;
    }

    // ---------------------------------------------------------------- agent control (Windows)

    private const string AgentExe = "HashRelay.Agent.exe";

    private static Process? AgentProcess()
        => Process.GetProcessesByName("HashRelay.Agent").FirstOrDefault();

    private static string? FindAgent()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, AgentExe),
            Path.Combine(AppContext.BaseDirectory, "..", AgentExe),
            Path.Combine(AppContext.BaseDirectory, "..", "agent", AgentExe),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "HashRelay", AgentExe),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static Task<int> AgentAsync(Args a, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult(Terminal.Fail(ExitCodes.InvalidInput, "The tray agent is Windows-only. Use `hashrelay listen` here."));
        var sub = a.Positional(1)?.ToLowerInvariant() ?? "status";
        var proc = AgentProcess();
        switch (sub)
        {
            case "status":
                Terminal.Line(proc is null ? Terminal.Yellow("Agent is not running.") : Terminal.Green($"Agent is running (pid {proc.Id})."));
                Terminal.Emit(new { ok = true, running = proc is not null, pid = proc?.Id, path = FindAgent() });
                return Task.FromResult(ExitCodes.Ok);
            case "start":
                if (proc is not null) { Terminal.Info($"Agent already running (pid {proc.Id})."); return Task.FromResult(ExitCodes.Ok); }
                var exe = FindAgent();
                if (exe is null) return Task.FromResult(Terminal.Fail(ExitCodes.Rejected, "HashRelay.Agent.exe was not found.", "Install the Windows agent from the Downloads page."));
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                Terminal.Ok("Agent started. Look for the # icon in the system tray.");
                Terminal.Emit(new { ok = true, started = true, path = exe });
                return Task.FromResult(ExitCodes.Ok);
            case "stop":
                if (proc is null) { Terminal.Info("Agent is not running."); return Task.FromResult(ExitCodes.Ok); }
                try { proc.Kill(entireProcessTree: true); proc.WaitForExit(5000); Terminal.Ok("Agent stopped."); }
                catch (Exception ex) { return Task.FromResult(Terminal.Fail(ExitCodes.Unexpected, $"Could not stop the agent: {ex.Message}")); }
                Terminal.Emit(new { ok = true, stopped = true });
                return Task.FromResult(ExitCodes.Ok);
            default:
                return Task.FromResult(Terminal.Fail(ExitCodes.InvalidInput, $"Unknown agent command '{sub}'.", "Use: hashrelay agent start|stop|status"));
        }
    }

    private static int Open(Args a)
    {
        var cfg = LoadConfig(a);
        var url = new Uri(cfg.ServerUri, "dashboard").ToString();
        TryOpenBrowser(url);
        Terminal.Ok($"Opened {Terminal.Cyan(url)}");
        return ExitCodes.Ok;
    }
}
