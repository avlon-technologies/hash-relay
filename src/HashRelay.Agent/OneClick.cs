using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using HashRelay.Client;
using HashRelay.Contracts;
using Microsoft.Toolkit.Uwp.Notifications;

namespace HashRelay.Agent;

/// <summary>DPAPI, current Windows user: only this user on this machine can unseal the decision grant.</summary>
internal sealed class DpapiGrantProtector : IGrantProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HashRelay.DecisionGrant.v1");
    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] sealedBytes) => ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>One-click approvals on this device: enrolment, the decision on a toast click, and the result toast.</summary>
internal static class OneClick
{
    public static DecisionGrantStore Store { get; } = new(DecisionGrantStore.DefaultPath, new DpapiGrantProtector());
    private static readonly ConcurrentDictionary<Guid, byte> InFlight = new();

    public static bool IsEnrolled(RelayConfig config)
        => config.IsLoggedIn && Store.LoadFor(config.ServerUri, DateTimeOffset.UtcNow) is not null;

    private static HashRelayApi Api(RelayConfig config) => new(config.ServerUri, config.ApiKey, userAgent: $"hashrelay-agent/{AgentInfo.Version}");

    /// <summary>
    /// Handles an Approve/Reject click. The toast first becomes "Submitting…" with no buttons, then shows only what the
    /// server confirmed. A click while the same approval is already being submitted is ignored; a click in another
    /// process (Windows started a fresh agent) reuses the same Idempotency-Key, so the server records at most one decision.
    /// </summary>
    public static async Task DecideAsync(Guid approvalId, string decision, Guid deliveryId, string? tag, string? subject)
    {
        if (!InFlight.TryAdd(approvalId, 0)) { Log.Info($"One-click {decision} for {approvalId} ignored: already submitting"); return; }
        try
        {
            RelayConfig? config = null;
            var result = await OneClickFlow.RunAsync(
                submit: async () =>
                {
                    config = ConfigStore.Load();
                    if (!config.IsLoggedIn) return new DecisionResult(DecisionResultKind.NotAuthorized, "This device isn't paired. Open HashRelay to decide.");
                    var decider = new ApprovalDecider(() => Api(config), config.ServerUri, Store);
                    return await decider.DecideAsync(approvalId, decision, deliveryId);
                },
                showProgress: () => Show(BuildProgress(subject, decision), tag, popup: false),
                showResult: r => Show(BuildResult(subject, r, tag, approvalId, decision, deliveryId, ApprovalPageUrl(config ?? ConfigStore.Load(), approvalId)), tag, popup: true),
                logError: (what, ex) => Log.Error(what, ex));
            Log.Info($"One-click {decision} for {approvalId}: {result.Kind} {result.Status}");
        }
        finally { InFlight.TryRemove(approvalId, out _); }
    }

    private static string ApprovalPageUrl(RelayConfig config, Guid approvalId) => new Uri(config.ServerUri, $"approvals/{approvalId}").ToString();

    internal static ToastContentBuilder BuildProgress(string? subject, string decision)
        => Base(subject, decision == ApprovalDecisions.Approve ? "Approving…" : "Rejecting…", "Submitting your decision…").AddArgument("action", "none");

    internal static ToastContentBuilder BuildResult(string? subject, DecisionResult r, string? tag, Guid approvalId, string decision, Guid deliveryId, string page)
    {
        var b = Base(subject, r.Heading, r.Message);
        if (r.Retryable)
        {
            var retry = new ToastButton().SetContent("Retry");
            foreach (var (k, v) in ApprovalToastArgs.For(approvalId, decision, deliveryId)) retry.AddArgument(k, v);
            retry.AddArgument("tag", tag ?? "").AddArgument("subject", subject ?? "");
            b.AddButton(retry);
        }
        if (!r.Decided)
            b.AddButton(new ToastButton().SetContent("Open HashRelay").AddArgument("action", "url").AddArgument("url", page));
        b.AddArgument("action", "url").AddArgument("url", page);
        return b;
    }

    /// <summary>
    /// Heading and subject on one line each, then the message on up to two. The toolkit throws for hintMaxLines above 2
    /// on any text, and for more than 4 lines in total (a heading defaults to 2), so the budget is 1 + 1 + 2.
    /// </summary>
    private static ToastContentBuilder Base(string? subject, string heading, string body)
    {
        var b = new ToastContentBuilder().AddText(heading, hintMaxLines: 1);
        if (!string.IsNullOrWhiteSpace(subject)) b.AddText(subject, hintMaxLines: 1);
        b.AddText(body, hintMaxLines: 2).AddAudio(new ToastAudio { Silent = true });
        var icon = Path.Combine(AppContext.BaseDirectory, "hashrelay.png");
        if (File.Exists(icon)) b.AddAppLogoOverride(new Uri(icon), ToastGenericAppLogoCrop.None);
        return b;
    }

    /// <summary>Same tag and group as the approval toast, so the state replaces it in Notification Center. Throws on failure; OneClickFlow logs it.</summary>
    private static void Show(ToastContentBuilder b, string? tag, bool popup)
        => b.Show(t =>
        {
            if (!string.IsNullOrEmpty(tag)) { t.Tag = tag; t.Group = "hashrelay"; }
            t.SuppressPopup = !popup;
            t.ExpirationTime = DateTimeOffset.Now.AddDays(1);
        });

    // ------------------------------------------------------------------ enrolment

    public static async Task EnableAsync(RelayConfig config, CancellationToken ct)
    {
        using var api = Api(config);
        var grant = await DecisionGrantEnrollment.RunAsync(api, config.ServerUri, Store, (uri, code) =>
        {
            Log.Info($"One-click enrolment: opening {uri} (code {code})");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }, ct);
        if (grant is null) Toasts.ShowInfo("One-click approvals not enabled", "Nothing was granted. Approval notifications keep opening HashRelay to decide.");
        else Toasts.ShowInfo("One-click approvals on", $"Approve and Reject on notifications now record your decision{(grant.AccountName is null ? "" : $" as {grant.AccountName}")}. Turn it off from the tray menu.");
    }

    public static async Task DisableAsync(RelayConfig config, CancellationToken ct)
    {
        try
        {
            using var api = Api(config);
            await DecisionGrantEnrollment.DisableAsync(api, Store, ct);
            Toasts.ShowInfo("One-click approvals off", "Approval notifications will open HashRelay to decide.");
        }
        catch (Exception ex)
        {
            Log.Error("Disable one-click", ex);
            Toasts.ShowInfo("One-click approvals off on this PC", "The server could not be reached to revoke it; it is removed from this PC. Revoke it on the website too: Approvals › One-click approvals.",
                new Uri(config.ServerUri, "approvals/devices").ToString());
        }
    }
}
