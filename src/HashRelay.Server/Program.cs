using System.Threading.RateLimiting;
using HashRelay.Contracts;
using HashRelay.Server.Api;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using HashRelay.Server.Realtime;
using HashRelay.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.Configure<HashRelayOptions>(config.GetSection("HashRelay"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();

// ---- storage: SQLite file (App Service persists /home; locally ./data) ----
var dbPath = config["HashRelay:DatabasePath"];
if (string.IsNullOrWhiteSpace(dbPath))
{
    var home = Environment.GetEnvironmentVariable("HOME");
    var dataDir = !string.IsNullOrEmpty(home) && Directory.Exists(home) && !OperatingSystem.IsWindows()
        ? Path.Combine(home, "data")
        : Path.Combine(builder.Environment.ContentRootPath, "data");
    dbPath = Path.Combine(dataDir, "hashrelay.db");
}
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContext<RelayDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

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
builder.Services.AddScoped<DevicePairingService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
    await db.Database.EnsureCreatedAsync();
    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")))
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
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

public partial class Program;
