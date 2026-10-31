using System.Net;
using System.Text.RegularExpressions;
using HashRelay.Server.Services;
using HashRelay.Server.Services.Email;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>Self-service "forgot password": emailed single-use links, through the real pages with a captured outbox.</summary>
public sealed class PasswordResetTests : IAsyncLifetime
{
    private sealed class Outbox : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public bool IsConfigured => true;
        public Task SendAsync(EmailMessage message, CancellationToken ct) { lock (Sent) Sent.Add(message); return Task.CompletedTask; }
    }

    private readonly Outbox _outbox = new();
    private readonly RelayFactory _factory;
    private RelayFactory.Workspace _ws = null!;

    public PasswordResetTests() => _factory = new RelayFactory { TestServices = s => s.AddSingleton<IEmailSender>(_outbox) };

    public async ValueTask InitializeAsync() => _ws = await _factory.CreateWorkspaceAsync("Reset Co");
    public ValueTask DisposeAsync() { _factory.Dispose(); return ValueTask.CompletedTask; }

    private static string AntiForgery(string html)
        => Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private static string LinkIn(EmailMessage m)
        => Regex.Match(m.Text, @"http://localhost/reset-password\?token=[A-Za-z0-9]+").Value;

    [Fact]
    public async Task Forgot_form_emails_a_link_that_sets_a_new_password_and_signs_in()
    {
        var c = _factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });

        var form = await c.GetStringAsync("/forgot-password");
        Assert.Contains("Send reset link", form);
        var res = await c.PostAsync("/forgot-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = _ws.Email.ToUpperInvariant(), ["__RequestVerificationToken"] = AntiForgery(form),
        }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Check your email", await res.Content.ReadAsStringAsync());

        var mail = Assert.Single(_outbox.Sent);
        Assert.Equal(_ws.Email, mail.To);
        var link = LinkIn(mail);
        Assert.False(string.IsNullOrEmpty(link), "reset link missing from email: " + mail.Text);
        Assert.NotNull(mail.Html);

        var resetPage = await c.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, resetPage.StatusCode);
        var resetHtml = await resetPage.Content.ReadAsStringAsync();
        Assert.Contains("Choose a new password", resetHtml);
        Assert.Contains(_ws.Email, resetHtml);

        var token = Regex.Match(link, "token=([A-Za-z0-9]+)").Groups[1].Value;
        var done = await c.PostAsync("/reset-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Token"] = token, ["Input.Password"] = "a-fresh-password-1", ["Input.Confirm"] = "a-fresh-password-1", ["__RequestVerificationToken"] = AntiForgery(resetHtml),
        }));
        Assert.Equal(HttpStatusCode.Found, done.StatusCode);
        Assert.StartsWith("/dashboard", done.Headers.Location!.ToString());

        // signed in by the reset, and the new password is the only one that works
        var settings = await c.GetAsync("/dashboard/settings");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        Assert.Null(await accounts.SignInAsync(_ws.Email, _ws.Password, CancellationToken.None));
        Assert.NotNull(await accounts.SignInAsync(_ws.Email, "a-fresh-password-1", CancellationToken.None));

        // the link is single use
        var again = await c.GetStringAsync(link);
        Assert.Contains("This link has expired", again);
    }

    [Fact]
    public async Task Unknown_email_gets_the_same_page_and_no_email()
    {
        var c = _factory.CreateClient(new() { HandleCookies = true });
        var form = await c.GetStringAsync("/forgot-password");
        var res = await c.PostAsync("/forgot-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = "nobody@test.local", ["__RequestVerificationToken"] = AntiForgery(form),
        }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Check your email", await res.Content.ReadAsStringAsync());
        Assert.Empty(_outbox.Sent);
    }

    [Fact]
    public async Task Reset_form_rejects_mismatch_short_and_unknown_tokens()
    {
        var c = _factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        using (var scope = _factory.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<PasswordResetService>().RequestAsync(_ws.Email, "127.0.0.1", CancellationToken.None));
        var token = Regex.Match(LinkIn(Assert.Single(_outbox.Sent)), "token=([A-Za-z0-9]+)").Groups[1].Value;
        var page = await c.GetStringAsync("/reset-password?token=" + token);

        async Task<string> Post(string t, string pw, string confirm)
        {
            var r = await c.PostAsync("/reset-password", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Token"] = t, ["Input.Password"] = pw, ["Input.Confirm"] = confirm, ["__RequestVerificationToken"] = AntiForgery(page),
            }));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        }

        Assert.Contains("do not match", await Post(token, "a-fresh-password-1", "a-different-one-2"));
        Assert.Contains("at least 10", await Post(token, "short", "short"));
        Assert.Contains("This link has expired", await Post(new string('x', 48), "a-fresh-password-1", "a-fresh-password-1"));
        Assert.Contains("This link has expired", await c.GetStringAsync("/reset-password?token=garbage"));

        // nothing above consumed the real token or changed the password
        using var verify = _factory.Services.CreateScope();
        var accounts = verify.ServiceProvider.GetRequiredService<AccountService>();
        Assert.NotNull(await accounts.SignInAsync(_ws.Email, _ws.Password, CancellationToken.None));
        Assert.NotNull(await verify.ServiceProvider.GetRequiredService<PasswordResetService>().PeekAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task New_link_supersedes_old_and_requests_are_throttled_per_account()
    {
        using var scope = _factory.Services.CreateScope();
        var resets = scope.ServiceProvider.GetRequiredService<PasswordResetService>();

        Assert.True(await resets.RequestAsync(_ws.Email, null, CancellationToken.None));
        Assert.True(await resets.RequestAsync(_ws.Email, null, CancellationToken.None));
        Assert.Equal(2, _outbox.Sent.Count);
        var first = Regex.Match(LinkIn(_outbox.Sent[0]), "token=([A-Za-z0-9]+)").Groups[1].Value;
        var second = Regex.Match(LinkIn(_outbox.Sent[1]), "token=([A-Za-z0-9]+)").Groups[1].Value;
        Assert.Null(await resets.PeekAsync(first, CancellationToken.None));
        Assert.NotNull(await resets.PeekAsync(second, CancellationToken.None));

        Assert.True(await resets.RequestAsync(_ws.Email, null, CancellationToken.None));
        Assert.False(await resets.RequestAsync(_ws.Email, null, CancellationToken.None)); // 4th within the hour
        Assert.Equal(PasswordResetService.MaxRequestsPerHour, _outbox.Sent.Count);
    }
}
