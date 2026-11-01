using HashRelay.Client;
using HashRelay.Contracts;
using HashRelay.Server.Data;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>
/// Regression: the agent built its "Approving…" toast with hintMaxLines: 3, the toolkit threw before the API call, and
/// the click recorded nothing. Rendering is now presentation only: whatever a toast does, the decision is submitted
/// and the outcome is the one the server confirmed.
/// </summary>
public sealed partial class OneClickApprovalTests
{
    private sealed class Recorder
    {
        public List<DecisionResult> Shown { get; } = [];
        public List<string> Errors { get; } = [];
        public int Submits;
        public void Log(string what, Exception ex) => Errors.Add($"{what}: {ex.GetType().Name}");
    }

    /// <summary>A paired, enrolled PC with one pending approval, and a submit that goes through the real client to the real server.</summary>
    private async Task<(ApprovalResource Approval, Func<string, Recorder, Func<Task<DecisionResult>>> Submit)> ClickReadyAsync()
    {
        var pc = await PairOnlineAsync(_ws);
        var store = NewStore();
        Task? browser = null;
        using (var api = ClientApi(pc.Token))
            Assert.NotNull(await DecisionGrantEnrollment.RunAsync(api, new Uri("http://localhost/"), store, (_, code) => browser = GrantInBrowserAsync(_ws, code),
                CancellationToken.None, interval: TimeSpan.FromMilliseconds(50)));
        await browser!;
        var a = await CreateApprovalAsync();
        var toast = await ToastForAsync(pc, a.Id);
        var decider = new ApprovalDecider(() => ClientApi(pc.Token), new Uri("http://localhost/"), store);
        return (a, (decision, rec) => () => { Interlocked.Increment(ref rec.Submits); return decider.DecideAsync(a.Id, decision, toast.DeliveryId); });
    }

    private static Exception ToolkitRefusal() => new ArgumentOutOfRangeException("hintMaxLines", "max line can't go more than 2 lines.");

    [Fact]
    public async Task A_progress_toast_that_fails_to_render_does_not_stop_the_decision()
    {
        var (a, submit) = await ClickReadyAsync();
        var rec = new Recorder();

        var result = await OneClickFlow.RunAsync(submit(ApprovalDecisions.Approve, rec), showProgress: () => throw ToolkitRefusal(), showResult: rec.Shown.Add, rec.Log);

        Assert.Equal(1, rec.Submits);
        Assert.Equal(DecisionResultKind.Approved, result.Kind);
        Assert.Equal(ApprovalStatuses.Approved, (await GetAsync(a.Id)).Status);
        Assert.Equal("Approved", Assert.Single(rec.Shown).Heading);
        Assert.Equal(["One-click progress toast: ArgumentOutOfRangeException"], rec.Errors);
    }

    [Fact]
    public async Task A_result_toast_that_fails_to_render_does_not_change_the_confirmed_outcome()
    {
        var (a, submit) = await ClickReadyAsync();
        var rec = new Recorder();

        var result = await OneClickFlow.RunAsync(submit(ApprovalDecisions.Reject, rec), showProgress: () => { }, showResult: _ => throw ToolkitRefusal(), rec.Log);

        Assert.Equal(DecisionResultKind.Rejected, result.Kind);
        Assert.Equal(ApprovalStatuses.Rejected, (await GetAsync(a.Id)).Status);
        Assert.Equal(["One-click result toast: ArgumentOutOfRangeException"], rec.Errors);

        // Clicked again because nothing appeared: same Idempotency-Key, the same outcome, no second decision.
        var again = await OneClickFlow.RunAsync(submit(ApprovalDecisions.Reject, rec), () => { }, rec.Shown.Add, rec.Log);
        Assert.Equal(DecisionResultKind.Rejected, again.Kind);
        Assert.Equal(2, (await GetAsync(a.Id)).Version);
        Assert.Single(Audit(a.Id), x => x.Action is ApprovalAuditActions.Approved or ApprovalAuditActions.Rejected);
    }

    [Fact]
    public async Task Both_toasts_failing_still_records_exactly_one_decision()
    {
        var (a, submit) = await ClickReadyAsync();
        var rec = new Recorder();

        var result = await OneClickFlow.RunAsync(submit(ApprovalDecisions.Approve, rec), () => throw ToolkitRefusal(), _ => throw ToolkitRefusal(), rec.Log);

        Assert.Equal(DecisionResultKind.Approved, result.Kind);
        Assert.Equal(1, rec.Submits);
        Assert.Equal(2, rec.Errors.Count);
        Assert.Equal(2, (await GetAsync(a.Id)).Version);
    }

    [Theory]
    [InlineData(ApprovalDecisions.Approve, DecisionResultKind.Approved, "Approved", ApprovalStatuses.Approved)]
    [InlineData(ApprovalDecisions.Reject, DecisionResultKind.Rejected, "Rejected", ApprovalStatuses.Rejected)]
    public async Task Approve_and_reject_each_show_their_confirmed_result(string decision, DecisionResultKind kind, string heading, string status)
    {
        var (a, submit) = await ClickReadyAsync();
        var rec = new Recorder();
        var progressShown = false;

        var result = await OneClickFlow.RunAsync(submit(decision, rec), () => progressShown = true, rec.Shown.Add, rec.Log);

        Assert.True(progressShown);
        var shown = Assert.Single(rec.Shown);
        Assert.Equal((kind, heading, true, false), (shown.Kind, shown.Heading, shown.Decided, shown.Retryable));
        Assert.Same(result, shown);
        Assert.Equal(status, (await GetAsync(a.Id)).Status);
        Assert.Empty(rec.Errors);
    }

    [Fact]
    public async Task An_exception_while_submitting_is_shown_as_unable_to_submit_never_as_success()
    {
        var rec = new Recorder();
        var result = await OneClickFlow.RunAsync(() => throw new InvalidOperationException("config unreadable"), () => { }, rec.Shown.Add, rec.Log);

        Assert.Equal(DecisionResultKind.Failed, result.Kind);
        var shown = Assert.Single(rec.Shown);
        Assert.Equal(("Unable to submit", false, true), (shown.Heading, shown.Decided, shown.Retryable));
        Assert.Equal(["One-click decision: InvalidOperationException"], rec.Errors);
    }
}
