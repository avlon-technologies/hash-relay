using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Services.Email;

/// <summary>One outbound transactional email. Plain text is mandatory; HTML is an optional richer alternative.</summary>
public sealed record EmailMessage(string To, string Subject, string Text, string? Html = null);

/// <summary>Outbound email. Configured through <c>HashRelay:Email</c>; without SMTP settings the server only logs what it would have sent.</summary>
public interface IEmailSender
{
    /// <summary>False when no delivery channel is configured; callers may warn operators but must still behave as if sent.</summary>
    bool IsConfigured { get; }
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed class EmailOptions
{
    /// <summary>Sender address, e.g. "HashRelay &lt;no-reply@hashrelay.dev.avlon.ca&gt;". Required for SMTP delivery.</summary>
    public string From { get; set; } = "";
    public SmtpOptions Smtp { get; set; } = new();

    public sealed class SmtpOptions
    {
        public string Host { get; set; } = "";
        /// <summary>Submission port. 587 with STARTTLS is what every major provider accepts; implicit TLS on 465 is not supported.</summary>
        public int Port { get; set; } = 587;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public bool UseStartTls { get; set; } = true;
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
    }
}

/// <summary>Sends through an SMTP submission endpoint (Microsoft 365, Google Workspace, SendGrid, Resend, Postmark …).</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public bool IsConfigured => options.Value.Smtp.IsConfigured && !string.IsNullOrWhiteSpace(options.Value.From);

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;
        if (!IsConfigured) throw new InvalidOperationException("SMTP is not configured (HashRelay:Email:From and HashRelay:Email:Smtp:Host).");

        using var mail = new MailMessage(new MailAddress(o.From), new MailAddress(message.To))
        {
            Subject = message.Subject,
            Body = message.Text,
            IsBodyHtml = false,
        };
        if (message.Html is not null)
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Html, null, "text/html"));

        using var client = new SmtpClient(o.Smtp.Host, o.Smtp.Port)
        {
            EnableSsl = o.Smtp.UseStartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 20_000,
        };
        if (!string.IsNullOrEmpty(o.Smtp.Username))
            client.Credentials = new NetworkCredential(o.Smtp.Username, o.Smtp.Password);

        await client.SendMailAsync(mail, ct);
        log.LogInformation("Sent email \"{Subject}\" to {To}", message.Subject, Redact(message.To));
    }

    internal static string Redact(string email)
    {
        var at = email.IndexOf('@');
        return at <= 1 ? "***" : email[0] + "***" + email[(at - 1)..];
    }
}

/// <summary>Used when no SMTP host is configured: logs the message at Warning so a local developer can still follow the link.</summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> log) : IEmailSender
{
    public bool IsConfigured => false;

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        log.LogWarning("Email delivery is not configured (HashRelay:Email:Smtp:Host). Would have sent to {To}: {Subject}\n{Text}", message.To, message.Subject, message.Text);
        return Task.CompletedTask;
    }
}
