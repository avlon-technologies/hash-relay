using System.Text.RegularExpressions;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Services;

public sealed partial class AccountService(RelayDbContext db, TimeProvider clock, ILogger<AccountService> log)
{
    public const int MinPasswordLength = 10;

    [GeneratedRegex("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$")]
    private static partial Regex EmailShape();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static string? ValidateEmail(string? email)
        => string.IsNullOrWhiteSpace(email) || !EmailShape().IsMatch(email.Trim()) || email.Length > 254 ? "Enter a valid email address." : null;

    public static string? ValidatePassword(string? password)
        => string.IsNullOrEmpty(password) || password.Length < MinPasswordLength ? $"Use at least {MinPasswordLength} characters." : null;

    public async Task<(Account Account, Tenant Tenant)> SignUpAsync(string email, string displayName, string password, string workspaceName, CancellationToken ct)
    {
        email = NormalizeEmail(email);
        if (await db.Accounts.AnyAsync(a => a.Email == email, ct))
            throw ApiException.Conflict("An account with that email already exists.");

        var now = clock.GetUtcNow();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = displayName.Trim(),
            PasswordHash = Secrets.HashPassword(password),
            CreatedAt = now,
            LastLoginAt = now,
        };
        db.Accounts.Add(account);
        var tenant = await CreateTenantCoreAsync(account.Id, workspaceName, now, ct);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Account {AccountId} signed up and created workspace {TenantSlug}", account.Id, tenant.Slug);
        return (account, tenant);
    }

    public async Task<Account?> SignInAsync(string email, string password, CancellationToken ct)
    {
        email = NormalizeEmail(email);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Email == email, ct);
        if (account is null || !Secrets.VerifyPassword(password, account.PasswordHash))
        {
            // Burn comparable time on unknown accounts so timing does not reveal existence.
            if (account is null) Secrets.VerifyPassword(password, Secrets.HashPassword("decoy-password"));
            return null;
        }
        account.LastLoginAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<Tenant> CreateTenantAsync(Guid accountId, string name, CancellationToken ct)
    {
        var tenant = await CreateTenantCoreAsync(accountId, name, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
        return tenant;
    }

    private async Task<Tenant> CreateTenantCoreAsync(Guid ownerId, string name, DateTimeOffset now, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length is < 2 or > 80) throw ApiException.Validation("Workspace name must be 2–80 characters.", "workspace");
        var slug = await UniqueSlugAsync(name, ct);
        var tenant = new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = name, OwnerAccountId = ownerId, CreatedAt = now };
        db.Tenants.Add(tenant);
        db.Memberships.Add(new Membership { TenantId = tenant.Id, AccountId = ownerId, Role = Roles.Owner, CreatedAt = now });
        return tenant;
    }

    private async Task<string> UniqueSlugAsync(string name, CancellationToken ct)
    {
        var baseSlug = NonSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (baseSlug.Length < 3) baseSlug = "workspace";
        if (baseSlug.Length > 40) baseSlug = baseSlug[..40].Trim('-');
        var slug = baseSlug;
        var attempt = 0;
        while (await db.Tenants.AnyAsync(t => t.Slug == slug, ct) || db.Tenants.Local.Any(t => t.Slug == slug))
        {
            attempt++;
            slug = attempt < 3 ? $"{baseSlug}-{attempt + 1}" : $"{baseSlug}-{Secrets.RandomString(4).ToLowerInvariant()}";
        }
        return slug;
    }

    public async Task RenameTenantAsync(Guid tenantId, string name, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length is < 2 or > 80) throw ApiException.Validation("Workspace name must be 2–80 characters.", "name");
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, name), ct);
    }
}
