using System.Net;
using System.Text;
using System.Text.Json;
using HashRelay.Contracts;

namespace HashRelay.Client;

/// <summary>Encrypts the decision grant at rest. The Windows agent uses DPAPI (current user); tests use a stand-in.</summary>
public interface IGrantProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] sealedBytes);
}

/// <summary>What the device keeps after the person granted one-click approvals. Never written in plaintext.</summary>
public sealed record StoredDecisionGrant(string Server, string Secret, string? AccountName, DateTimeOffset? ExpiresAt);

/// <summary>
/// The decision grant, sealed with <see cref="IGrantProtector"/> in its own file next to config.json. Kept out of
/// config.json so the device token file never holds decision authority, and so "turn off" is a file delete.
/// </summary>
public sealed class DecisionGrantStore(string path, IGrantProtector protector)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string DefaultPath => Path.Combine(ConfigStore.Directory, "decision-grant.bin");

    public string FilePath => path;

    public StoredDecisionGrant? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var plain = protector.Unprotect(File.ReadAllBytes(path));
            return JsonSerializer.Deserialize<StoredDecisionGrant>(plain, Json);
        }
        catch (Exception)
        {
            return null; // unreadable (other user, corrupted, different machine): not enrolled
        }
    }

    /// <summary>The grant, if it belongs to <paramref name="server"/> and has not expired locally.</summary>
    public StoredDecisionGrant? LoadFor(Uri server, DateTimeOffset now)
    {
        var g = Load();
        if (g is null) return null;
        if (!SameServer(g.Server, server) || (g.ExpiresAt is { } e && e <= now)) return null;
        return g;
    }

    public void Save(StoredDecisionGrant grant)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(grant, Json)));
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static bool SameServer(string stored, Uri server)
        => Uri.TryCreate(stored, UriKind.Absolute, out var s) && Uri.Compare(s, server, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
}

public enum DecisionResultKind { Approved, Rejected, Expired, AlreadyResolved, Cancelled, NotAuthorized, NotEnrolled, Failed }

/// <summary>A server-confirmed outcome, or the reason there is none. Only Approved/Rejected mean this click decided it.</summary>
public sealed record DecisionResult(DecisionResultKind Kind, string Message, string? Status = null)
{
    public bool Decided => Kind is DecisionResultKind.Approved or DecisionResultKind.Rejected;
    /// <summary>True when the toast should offer Retry: nothing is known to have been recorded.</summary>
    public bool Retryable => Kind == DecisionResultKind.Failed;

    /// <summary>The result toast's heading.</summary>
    public string Heading => Kind switch
    {
        DecisionResultKind.Approved => "Approved",
        DecisionResultKind.Rejected => "Rejected",
        DecisionResultKind.Expired => "Expired",
        DecisionResultKind.Cancelled => "Cancelled",
        DecisionResultKind.AlreadyResolved => "Already resolved",
        DecisionResultKind.NotAuthorized or DecisionResultKind.NotEnrolled => "Can't decide from here",
        _ => "Unable to submit",
    };

    public static DecisionResult SubmitFailed { get; } = new(DecisionResultKind.Failed, "Unable to submit. Nothing was recorded. Retry, or open HashRelay to decide.");
}

/// <summary>
/// The order of a one-click: show progress, submit, show the outcome. Showing is presentation only, so a failure to
/// render either toast is logged and never stops the submission or changes the outcome the server confirmed.
/// </summary>
public static class OneClickFlow
{
    public static async Task<DecisionResult> RunAsync(Func<Task<DecisionResult>> submit, Action showProgress, Action<DecisionResult> showResult,
        Action<string, Exception> logError)
    {
        try { showProgress(); }
        catch (Exception ex) { logError("One-click progress toast", ex); }

        DecisionResult result;
        try { result = await submit(); }
        catch (Exception ex)
        {
            logError("One-click decision", ex);
            result = DecisionResult.SubmitFailed;
        }

        try { showResult(result); }
        catch (Exception ex) { logError("One-click result toast", ex); }
        return result;
    }
}

/// <summary>The toast button arguments for one-click decisions. Only ids, never a credential.</summary>
public static class ApprovalToastArgs
{
    public const string Action = "action";
    public const string Decide = "decide";
    public const string ApprovalId = "approvalId";
    public const string Decision = "decision";
    public const string DeliveryId = "deliveryId";

    public static IReadOnlyDictionary<string, string> For(Guid approvalId, string decision, Guid deliveryId)
        => new Dictionary<string, string> { [Action] = Decide, [ApprovalId] = approvalId.ToString("D"), [Decision] = decision, [DeliveryId] = deliveryId.ToString("D") };

    public static bool TryParse(IReadOnlyDictionary<string, string> args, out Guid approvalId, out string decision, out Guid deliveryId)
    {
        approvalId = Guid.Empty;
        deliveryId = Guid.Empty;
        decision = "";
        if (!args.TryGetValue(DeliveryId, out var del) || !Guid.TryParse(del, out deliveryId)) return false;
        if (!args.TryGetValue(Action, out var action) || action != Decide) return false;
        if (!args.TryGetValue(ApprovalId, out var id) || !Guid.TryParse(id, out approvalId)) return false;
        if (!args.TryGetValue(Decision, out var d) || d is not (ApprovalDecisions.Approve or ApprovalDecisions.Reject)) return false;
        decision = d;
        return true;
    }

    /// <summary>Same click, same key: a repeated or retried click can never record a second decision.</summary>
    public static string IdempotencyKey(Guid approvalId, string decision) => $"toast-{approvalId:N}-{decision}";
}

/// <summary>
/// Submits a one-click decision and reports only what the server confirmed. Stateless apart from the grant store,
/// so a process that Windows starts just to handle a toast click behaves exactly like the running agent.
/// </summary>
public sealed class ApprovalDecider(Func<HashRelayApi> api, Uri server, DecisionGrantStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public bool IsEnrolled => store.LoadFor(server, _clock.GetUtcNow()) is not null;

    public async Task<DecisionResult> DecideAsync(Guid approvalId, string decision, Guid deliveryId, CancellationToken ct = default)
    {
        var grant = store.LoadFor(server, _clock.GetUtcNow());
        if (grant is null) return new(DecisionResultKind.NotEnrolled, "One-click approvals aren't turned on for this device. Open HashRelay to decide.");
        try
        {
            using var client = api();
            var a = await client.DecideOnDeviceAsync(approvalId, decision, deliveryId, grant.Secret, ApprovalToastArgs.IdempotencyKey(approvalId, decision), ct);
            return a.Status switch
            {
                ApprovalStatuses.Approved => new(DecisionResultKind.Approved, "Approved. Your decision was recorded.", a.Status),
                ApprovalStatuses.Rejected => new(DecisionResultKind.Rejected, "Rejected. Your decision was recorded.", a.Status),
                _ => new(DecisionResultKind.AlreadyResolved, $"This request is {a.Status}.", a.Status),
            };
        }
        catch (HashRelayApiException ex)
        {
            return FromRefusal(ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return new(DecisionResultKind.Failed, "Unable to submit. Nothing was recorded. Retry, or open HashRelay to decide.");
        }
    }

    private DecisionResult FromRefusal(HashRelayApiException ex)
    {
        var details = ex.Error?.Details;
        switch (ex.Status)
        {
            case HttpStatusCode.Conflict when details is not null && details.TryGetValue("status", out var s) && s.Length > 0:
                return s[0] switch
                {
                    ApprovalStatuses.Expired => new(DecisionResultKind.Expired, "This request expired before a decision was recorded.", s[0]),
                    ApprovalStatuses.Cancelled => new(DecisionResultKind.Cancelled, "The requester cancelled this request.", s[0]),
                    _ => new(DecisionResultKind.AlreadyResolved, $"Already resolved: {s[0]}. Nothing new was recorded.", s[0]),
                };
            case HttpStatusCode.Unauthorized when details is not null && details.ContainsKey("grant"):
                store.Delete(); // revoked or expired on the server: stop offering one-click
                return new(DecisionResultKind.NotAuthorized, "One-click approval was turned off for this device. Open HashRelay to decide.");
            case HttpStatusCode.Unauthorized:
                return new(DecisionResultKind.NotAuthorized, "This device is no longer paired. Open HashRelay to decide.");
            case HttpStatusCode.Forbidden:
                return new(DecisionResultKind.NotAuthorized, "You are not a designated approver of this request.");
            case HttpStatusCode.NotFound:
                return new(DecisionResultKind.NotAuthorized, "This request isn't in your workspace, or you are no longer a member.");
            default:
                return new(DecisionResultKind.Failed, "Unable to submit. Nothing was recorded. Retry, or open HashRelay to decide.");
        }
    }
}

/// <summary>Enrolment: ask the server, send the person to the browser, wait for their grant, seal the secret.</summary>
public static class DecisionGrantEnrollment
{
    public static async Task<StoredDecisionGrant?> RunAsync(HashRelayApi api, Uri server, DecisionGrantStore store, Action<Uri, string> openBrowser,
        CancellationToken ct, TimeSpan? interval = null)
    {
        var start = await api.BeginDecisionGrantAsync(ct);
        openBrowser(new Uri(start.VerificationUri), start.UserCode);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(start.ExpiresInSeconds);
        var wait = interval ?? TimeSpan.FromSeconds(Math.Max(1, start.IntervalSeconds));
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var poll = await api.PollDecisionGrantAsync(start.PollCode, ct);
            switch (poll.Status)
            {
                case "granted" when poll.Grant is not null:
                    var g = new StoredDecisionGrant(server.ToString(), poll.Grant, poll.AccountName, poll.ExpiresAt);
                    store.Save(g);
                    return g;
                case "denied" or "expired":
                    return null;
            }
            await Task.Delay(wait, ct);
        }
        return null;
    }

    /// <summary>Turns it off on the server (best effort) and always locally.</summary>
    public static async Task DisableAsync(HashRelayApi api, DecisionGrantStore store, CancellationToken ct)
    {
        var g = store.Load();
        try { if (g is not null) await api.RevokeDecisionGrantAsync(g.Secret, ct); }
        finally { store.Delete(); }
    }
}
