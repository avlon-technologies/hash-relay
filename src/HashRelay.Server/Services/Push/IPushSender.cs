namespace HashRelay.Server.Services.Push;

/// <summary>A notification handed to a push provider for one endpoint.</summary>
/// <param name="CollapseId">Stable per-notification key so a repeated push replaces rather than duplicates on the device.</param>
/// <param name="Data">String-only payload the app reads: notificationId, deliveryId, tenantId, tenantName, tags, priority, publishedAt, detailUrl, rich, url?, imageUrl?.</param>
public sealed record PushMessage(
    string Token,
    string Platform,
    string Title,
    string Body,
    string? ImageUrl,
    string Priority,
    string CollapseId,
    IReadOnlyDictionary<string, string> Data);

public enum PushOutcome
{
    /// <summary>The provider accepted the message. This is provider acceptance only, never evidence of display.</summary>
    Accepted,
    /// <summary>The provider no longer knows the token (uninstalled app, rotated token); stop using it.</summary>
    InvalidToken,
    /// <summary>Transient or configuration failure; the delivery stays queued.</summary>
    Failed,
}

public sealed record PushResult(PushOutcome Outcome, string? ProviderMessageId, string? Error);

/// <summary>Transport to a push provider. One implementation per provider name in <see cref="Contracts.PushProviders"/>.</summary>
public interface IPushSender
{
    /// <summary>False until the provider credential is configured; routing then leaves push-capable endpoints queued for replay.</summary>
    bool Enabled { get; }
    /// <summary>The provider name endpoints register tokens under (e.g. "fcm").</summary>
    string Provider { get; }
    Task<PushResult> SendAsync(PushMessage message, CancellationToken ct);
}
