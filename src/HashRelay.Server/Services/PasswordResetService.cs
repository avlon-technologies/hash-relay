using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using HashRelay.Server.Services.Email;
using Microsoft.EntityFrameworkCore;

namespace HashRelay.Server.Services;

/// <summary>
/// Self-service "forgot password": issues a single-use, time-limited link by email and completes the reset from it.
/// Only a SHA-256 verifier of the token is stored, so a copy of the database cannot be used to take over accounts.
/// Requests never reveal whether an address has an account: unknown addresses and throttled accounts return
/// silently, and the page shows the same message either way.
/// </summary>
public sealed class PasswordResetService(RelayDbContext db, TimeProvider clock, IEmailSender mailer, PublicBase publicBase, ILogger<PasswordResetService> log)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    /// <summary>At most this many links per account per hour, so the form cannot be used to flood someone's inbox.</summary>
    public const int MaxRequestsPerHour = 3;
    private const int TokenLength = 48;

    /// <summary>Issues a link and emails it when the address belongs to an account and the account is not throttled. Returns whether a link was issued.</summary>
    public async Task<bool> RequestAsync(string email, string? ip, CancellationToken ct)
    {
        var normalized = AccountService.NormalizeEmail(email);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Email == normalized, ct);
        if (account is null)
        {
            log.LogInformation("Password reset requested for an unknown address");
            return false;
        }

        var now = clock.GetUtcNow();
        var recent = await db.PasswordResets.CountAsync(r => r.AccountId == account.Id && r.CreatedAt > now - TimeSpan.FromHours(1), ct);
        if (recent >= MaxRequestsPerHour)
        {
            log.LogWarning("Password reset for account {AccountId} throttled ({Count} requests in the last hour)", account.Id, recent);
            return false;
        }

        // Older outstanding links stop working as soon as a new one is issued.
        await db.PasswordResets
            .Where(r => r.AccountId == account.Id && r.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ConsumedAt, now), ct);

        var token = Secrets.RandomString(TokenLength);
        db.PasswordResets.Add(new PasswordReset
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            TokenHash = Secrets.Sha256Hex(token),
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
            RequestIp = ip,
        });
        await db.SaveChangesAsync(ct);

        var link = publicBase.Absolute("/reset-password?token=" + Uri.EscapeDataString(token));
        await mailer.SendAsync(Compose(account, link), ct);
        log.LogInformation("Password reset link issued for account {AccountId} (email delivery {Configured})", account.Id, mailer.IsConfigured ? "configured" : "NOT configured");
        return true;
    }

    /// <summary>The account a token currently unlocks, or null when the token is unknown, used or expired.</summary>
    public async Task<Account?> PeekAsync(string? token, CancellationToken ct)
    {
        var reset = await FindActiveAsync(token, ct);
        return reset is null ? null : await db.Accounts.FirstOrDefaultAsync(a => a.Id == reset.AccountId, ct);
    }

    /// <summary>Sets the new password and burns the token. Returns the account, or null when the token is not valid.</summary>
    public async Task<Account?> CompleteAsync(string? token, string newPassword, CancellationToken ct)
    {
        if (AccountService.ValidatePassword(newPassword) is { } problem) throw ApiException.Validation(problem, "password");
        var reset = await FindActiveAsync(token, ct);
        if (reset is null) return null;
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == reset.AccountId, ct);
        if (account is null) return null;

        var now = clock.GetUtcNow();
        account.PasswordHash = Secrets.HashPassword(newPassword);
        reset.ConsumedAt = now;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Account {AccountId} reset its password through an emailed link", account.Id);
        return account;
    }

    private async Task<PasswordReset?> FindActiveAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != TokenLength) return null;
        var hash = Secrets.Sha256Hex(token);
        var now = clock.GetUtcNow();
        return await db.PasswordResets.FirstOrDefaultAsync(r => r.TokenHash == hash && r.ConsumedAt == null && r.ExpiresAt > now, ct);
    }

    private static EmailMessage Compose(Account account, string link)
    {
        var hours = (int)Lifetime.TotalHours;
        var text = $"""
            Hi {account.DisplayName},

            Someone asked to reset the password for your HashRelay account ({account.Email}).
            Open this link to choose a new password. It works once and expires in {hours} hour{(hours == 1 ? "" : "s")}:

            {link}

            If you did not ask for this, you can ignore this email; your password has not changed.

            — HashRelay
            """;
        var html = $"""
            <p>Hi {System.Net.WebUtility.HtmlEncode(account.DisplayName)},</p>
            <p>Someone asked to reset the password for your HashRelay account ({System.Net.WebUtility.HtmlEncode(account.Email)}).
            Open the link below to choose a new password. It works once and expires in {hours} hour{(hours == 1 ? "" : "s")}.</p>
            <p><a href="{link}">Reset your password</a></p>
            <p style="color:#666;font-size:13px">If the button does not work, paste this into your browser:<br>{link}</p>
            <p style="color:#666;font-size:13px">If you did not ask for this, you can ignore this email; your password has not changed.</p>
            """;
        return new EmailMessage(account.Email, "Reset your HashRelay password", text, html);
    }
}
