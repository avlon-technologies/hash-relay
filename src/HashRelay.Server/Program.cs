using System.Threading.RateLimiting;
using HashRelay.Contracts;
using HashRelay.Server.Api;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using HashRelay.Server.Realtime;
using HashRelay.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.Configure<HashRelayOptions>(config.GetSection("HashRelay"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();

// ---- storage: SQLite file. In Azure the data directory is a mounted file share (/data); locally ./data ----
var dbPath = config["HashRelay:DatabasePath"];
if (string.IsNullOrWhiteSpace(dbPath))
{
    var home = Environment.GetEnvironmentVariable("HOME");
    var dataDir = !string.IsNullOrEmpty(home) && Directory.Exists(home) && !OperatingSystem.IsWindows()
        ? Path.Combine(home, "data")
        : Path.Combine(builder.Environment.ContentRootPath, "data");
    dbPath = Path.Combine(dataDir, "hashrelay.db");
}
var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDbContext<RelayDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

// Cookie/antiforgery keys must survive restarts and scale-to-zero wake-ups, so persist them next to the database.
builder.Services.AddDataProtection()
    .SetApplicationName("HashRelay")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));

// WAL journaling is unsafe on network file systems (Azure Files / App Service share); default to it only on local disk.
var useWal = config.GetValue<bool?>("HashRelay:UseWal")
    ?? (Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") is null && Environment.GetEnvironmentVariable("CONTAINER_APP_NAME") is null);

// ---- web (dashboard) + api ----
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Dashboard");
    o.Conventions.AuthorizePage("/Pair");
});
builder.Services.AddSignalR(o => { o.EnableDetailedErrors = builder.Environment.IsDevelopment(); o.MaximumReceiveMessageSize = 64 * 1024; });

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, o =>
    {
        o.Cookie.Name = "hr_session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.LoginPath = "/login";
        o.LogoutPath = "/logout";
        o.AccessDeniedPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiAuth.Scheme, null);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ApiAuth.PolicyAny, p => p.AddAuthenticationSchemes(ApiAuth.Scheme).RequireAuthenticatedUser())
    .AddPolicy(ApiAuth.PolicyPublish, p => p.AddAuthenticationSchemes(ApiAuth.Scheme).RequireAuthenticatedUser().RequireClaim(ApiAuth.ClaimScope, Scopes.Publish))
    .AddPolicy(ApiAuth.PolicySubscribe, p => p.AddAuthenticationSchemes(ApiAuth.Scheme).RequireAuthenticatedUser().RequireClaim(ApiAuth.ClaimScope, Scopes.Subscribe));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        await ApiErrors.WriteAsync(ctx.HttpContext, StatusCodes.Status429TooManyRequests, ErrorCodes.Throttled, "Rate limit exceeded. Try again shortly.");
    };
    var perMinute = Math.Max(10, config.GetValue("HashRelay:PublishRateLimitPerMinute", 120));
    o.AddPolicy("publish", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirst(ApiAuth.ClaimCredentialId)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("pairing", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<CurrentTenant>();
builder.Services.AddScoped<CredentialService>();
builder.Services.AddScoped<EndpointService>();
builder.Services.AddScoped<RoutingService>();
builder.Services.AddScoped<PublicBase>();
builder.Services.AddSingleton<ImageStore>();
builder.Services.Configure<DiagramOptions>(config.GetSection("HashRelay:Diagrams"));
builder.Services.AddHttpClient(HttpDiagramRenderer.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("hashrelay-server/" + HashRelay.Server.Api.ApiEndpoints.Version);
    c.MaxResponseContentBufferSize = Limits.ImageMaxBytes;
});
builder.Services.AddSingleton<IDiagramRenderer, HttpDiagramRenderer>();
builder.Services.AddScoped<DevicePairingService>();

var app = builder.Build();

// Operator maintenance: `HashRelay.Server reset-password EMAIL [PASSWORD]` sets a new password and exits without serving.
if (args is ["reset-password", var resetEmail, ..])
{
    using var scope = app.Services.CreateScope();
    var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
    var newPassword = args.Length > 2 ? args[2] : Secrets.TemporaryPassword();
    if (!await accounts.ResetPasswordAsync(resetEmail, newPassword, CancellationToken.None))
    {
        Console.Error.WriteLine($"No account with email {resetEmail}.");
        return 2;
    }
    Console.WriteLine($"Password for {AccountService.NormalizeEmail(resetEmail)} reset to: {newPassword}");
    return 0;
}

// Schema setup retries for a while: on a rolling deploy the previous revision may still hold the SQLite file on
// the shared volume for a few seconds (busy / locked / can't open), and failing here would crash-loop the new one.
for (var attempt = 1; ; attempt++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        await db.Database.EnsureCreatedAsync();
        await SchemaUpgrades.ApplyAsync(db);
        if (useWal) await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        break;
    }
    catch (Microsoft.Data.Sqlite.SqliteException ex) when (attempt < 15 && ex.SqliteErrorCode is 5 or 6 or 14) // SQLITE_BUSY, SQLITE_LOCKED, SQLITE_CANTOPEN
    {
        app.Logger.LogWarning("Database not ready ({Message}); retrying in 3s (attempt {Attempt}/15)", ex.Message, attempt);
        await Task.Delay(TimeSpan.FromSeconds(3));
    }
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}
app.UseStatusCodePagesWithReExecute("/error", "?code={0}");
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHashRelayApi();
app.MapDashboardEndpoints();
app.MapHub<NotificationsHub>(HubMethods.Path);
app.MapRazorPages();

app.Logger.LogInformation("HashRelay {Version} starting; database at {DbPath}", ApiEndpoints.Version, dbPath);
app.Run();
return 0;

public partial class Program;
