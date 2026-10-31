using System.Net.Http.Headers;
using System.Net.Http.Json;
using HashRelay.Contracts;
using HashRelay.Server.Data;
using HashRelay.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace HashRelay.Server.Tests;

/// <summary>Boots the real server against a throwaway SQLite file per test class.</summary>
public sealed class RelayFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hashrelay-test-{Guid.NewGuid():n}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("HashRelay:DatabasePath", _dbPath);
        builder.UseSetting("HashRelay:PublicUrl", "http://localhost");
        builder.UseSetting("HashRelay:ReplayWindowHours", "24");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Warning");
        builder.UseSetting("Logging:LogLevel:HashRelay", "Warning");
        if (Environment.GetEnvironmentVariable("HR_TEST_SQL") == "1") builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command", "Information");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    public sealed record Workspace(Guid TenantId, string Slug, Guid AccountId, string ApiKey, string SubscribeOnlyKey);

    /// <summary>Creates an account + tenant directly through the services and issues a publish+subscribe key and a subscribe-only key.</summary>
    public async Task<Workspace> CreateWorkspaceAsync(string name)
    {
        using var scope = Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        var creds = scope.ServiceProvider.GetRequiredService<CredentialService>();
        var (account, tenant) = await accounts.SignUpAsync($"{Guid.NewGuid():n}@test.local", "Tester", "correct-horse-battery", name, CancellationToken.None);
        var (_, full) = await creds.IssueApiKeyAsync(tenant.Id, account.Id, "full", [Scopes.Publish, Scopes.Subscribe], null, CancellationToken.None);
        var (_, sub) = await creds.IssueApiKeyAsync(tenant.Id, account.Id, "subscribe-only", [Scopes.Subscribe], null, CancellationToken.None);
        return new Workspace(tenant.Id, tenant.Slug, account.Id, full, sub);
    }

    public HttpClient ApiClient(string? key)
    {
        var c = CreateClient();
        if (key is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return c;
    }

    public HubConnection Hub(string key)
        => new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, HubMethods.Path), o =>
            {
                o.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(key);
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();
}

public static class HttpExtensions
{
    public static async Task<T> ReadJson<T>(this HttpResponseMessage res)
        => (await res.Content.ReadFromJsonAsync<T>())!;
}
