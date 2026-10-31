using System.Net;
using HashRelay.Server.Auth;
using HashRelay.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>Password change from Settings and the operator reset command's service path.</summary>
public sealed class AccountTests : IAsyncLifetime
{
    private readonly RelayFactory _factory = new();
    private RelayFactory.Workspace _ws = null!;

    public async ValueTask InitializeAsync() => _ws = await _factory.CreateWorkspaceAsync("Account Co");
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    [Fact]
    public async Task Change_password_requires_current_and_then_signs_in_with_new()
    {
        using var scope = _factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();

        var wrong = await Assert.ThrowsAsync<ApiException>(() => accounts.ChangePasswordAsync(_ws.AccountId, "not-the-password", "a-brand-new-password", CancellationToken.None));
        Assert.Equal(400, wrong.Status);
        var tooShort = await Assert.ThrowsAsync<ApiException>(() => accounts.ChangePasswordAsync(_ws.AccountId, _ws.Password, "short", CancellationToken.None));
        Assert.Equal(400, tooShort.Status);

        await accounts.ChangePasswordAsync(_ws.AccountId, _ws.Password, "a-brand-new-password", CancellationToken.None);
        Assert.Null(await accounts.SignInAsync(_ws.Email, _ws.Password, CancellationToken.None));
        Assert.NotNull(await accounts.SignInAsync(_ws.Email, "a-brand-new-password", CancellationToken.None));
    }

    [Fact]
    public async Task Operator_reset_replaces_password_for_known_email_only()
    {
        using var scope = _factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();

        Assert.False(await accounts.ResetPasswordAsync("nobody@test.local", "temporary-pass-1", CancellationToken.None));
        Assert.True(await accounts.ResetPasswordAsync(_ws.Email.ToUpperInvariant(), "temporary-pass-1", CancellationToken.None));
        Assert.NotNull(await accounts.SignInAsync(_ws.Email, "temporary-pass-1", CancellationToken.None));
    }

    [Fact]
    public async Task Settings_page_changes_password_through_the_form()
    {
        var browser = await _factory.LoginAsync(_ws);
        var page = await browser.GetStringAsync("/dashboard/settings");
        Assert.Contains("Change password", page);
        var token = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var res = await browser.PostAsync("/dashboard/settings?handler=Password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["currentPassword"] = _ws.Password, ["newPassword"] = "changed-via-form-1", ["confirmPassword"] = "changed-via-form-1", ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        Assert.NotNull(await accounts.SignInAsync(_ws.Email, "changed-via-form-1", CancellationToken.None));
    }
}
