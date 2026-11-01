using HashRelay.Client;
using HashRelay.Contracts;
using Xunit;

namespace HashRelay.Server.Tests;

/// <summary>The agent's presentation policy: live toasts at once, a replayed backlog as one summary, bounded urgent toasts, no duplicates.</summary>
public sealed class NotificationPresenterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static NotificationMessage Msg(bool replayed, string priority = "normal", string[]? tags = null, string? publisher = null, int minute = 0, Guid? id = null)
        => new(id ?? Guid.NewGuid(), Guid.Empty, tags ?? ["builds"], $"Message {minute}", null, T0.AddMinutes(minute), Guid.NewGuid(), null, priority, "Acme", replayed,
            Publisher: publisher is null ? null : new PublisherInfo(Guid.NewGuid(), publisher, PublisherTypes.Unknown, Channels.Rest));

    private sealed class Sink
    {
        public readonly List<(NotificationMessage Message, bool FromBacklog)> Shown = [];
        public readonly List<CatchUpSummary> Summaries = [];
        public NotificationPresenter Presenter(int maxUrgent = NotificationPresenter.DefaultMaxUrgentToasts, TimeSpan? settle = null)
            => new((m, b) => { lock (Shown) Shown.Add((m, b)); }, s => { lock (Summaries) Summaries.Add(s); }, maxUrgent, settle ?? Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void Live_notifications_are_shown_immediately()
    {
        var sink = new Sink();
        using var p = sink.Presenter();
        Assert.True(p.Receive(Msg(replayed: false)));
        var (m, fromBacklog) = Assert.Single(sink.Shown);
        Assert.False(fromBacklog);
        Assert.Empty(sink.Summaries);
        Assert.Equal(0, p.Pending);
    }

    [Fact]
    public void Replayed_backlog_produces_one_summary_and_no_individual_toasts()
    {
        var sink = new Sink();
        using var p = sink.Presenter();
        var publishers = new[] { "Claude Code", "CI", "Ops bot", "Mike's laptop" };
        var tags = new[] { "builds", "deploys", "alerts" };
        for (var i = 0; i < 47; i++) p.Receive(Msg(replayed: true, tags: [tags[i % 3]], publisher: publishers[i % 4], minute: i));
        Assert.Empty(sink.Shown);
        Assert.Equal(47, p.Pending);

        p.Flush();  // Hello completed
        Assert.Empty(sink.Shown);
        var s = Assert.Single(sink.Summaries);
        Assert.Equal(47, s.Count);
        Assert.Equal(47, s.Total);
        Assert.Equal(0, s.UrgentShown);
        Assert.Equal("47 notifications while you were away", s.Title);
        Assert.Equal("47 notifications from 4 publishers across 3 hashtags.", s.Describe());
        Assert.Equal(["alerts", "builds", "deploys"], s.Hashtags);
        Assert.Equal(0, p.Pending);
    }

    [Fact]
    public void A_single_backlog_item_is_simply_shown()
    {
        var sink = new Sink();
        using var p = sink.Presenter();
        p.Receive(Msg(replayed: true));
        p.Flush();
        var (_, fromBacklog) = Assert.Single(sink.Shown);
        Assert.True(fromBacklog);
        Assert.Empty(sink.Summaries);
    }

    [Fact]
    public void High_priority_backlog_gets_its_own_toasts_but_only_the_newest_few()
    {
        var sink = new Sink();
        using var p = sink.Presenter(maxUrgent: 3);
        for (var i = 0; i < 10; i++) p.Receive(Msg(replayed: true, priority: i < 5 ? Priorities.High : Priorities.Normal, minute: i));
        p.Flush();

        Assert.Equal(3, sink.Shown.Count);
        Assert.All(sink.Shown, s => { Assert.Equal(Priorities.High, s.Message.Priority); Assert.True(s.FromBacklog); });
        Assert.Equal(["Message 2", "Message 3", "Message 4"], sink.Shown.Select(s => s.Message.Title)); // the newest three, in order
        var summary = Assert.Single(sink.Summaries);
        Assert.Equal(7, summary.Count);          // the two older urgent ones are folded in rather than dropped
        Assert.Equal(3, summary.UrgentShown);
        Assert.Equal(10, summary.Total);
        Assert.Equal("10 notifications while you were away", summary.Title);
        Assert.Equal("7 notifications across 1 hashtag. 3 high-priority notifications shown separately.", summary.Describe());
    }

    [Fact]
    public void Urgent_cap_of_zero_folds_everything_into_the_summary()
    {
        var sink = new Sink();
        using var p = sink.Presenter(maxUrgent: 0);
        for (var i = 0; i < 4; i++) p.Receive(Msg(replayed: true, priority: Priorities.High, minute: i));
        p.Flush();
        Assert.Empty(sink.Shown);
        Assert.Equal(4, Assert.Single(sink.Summaries).Count);
    }

    [Fact]
    public void Flushing_again_or_reconnecting_with_nothing_pending_does_not_repeat_the_summary()
    {
        var sink = new Sink();
        using var p = sink.Presenter();
        for (var i = 0; i < 5; i++) p.Receive(Msg(replayed: true, minute: i));
        p.Flush();
        p.Flush();              // Hello again with no backlog
        p.Flush();
        Assert.Single(sink.Summaries);
        Assert.Empty(sink.Shown);
    }

    [Fact]
    public void Duplicates_by_notification_id_are_dropped_whatever_path_they_arrive_on()
    {
        var sink = new Sink();
        using var p = sink.Presenter();
        var id = Guid.NewGuid();
        Assert.True(p.Receive(Msg(replayed: false, id: id)));
        Assert.False(p.Receive(Msg(replayed: false, id: id)));   // live twice (two connections)
        Assert.False(p.Receive(Msg(replayed: true, id: id)));    // resent as backlog after a reconnect
        Assert.Single(sink.Shown);
        Assert.Equal(0, p.Pending);

        // The same notification arriving on two hashtags is still one notification.
        var multi = Guid.NewGuid();
        Assert.True(p.Receive(Msg(replayed: true, tags: ["a"], id: multi)));
        Assert.False(p.Receive(Msg(replayed: true, tags: ["b"], id: multi)));
        Assert.Equal(1, p.Pending);
    }

    [Fact]
    public void Live_notifications_are_still_immediate_while_a_backlog_is_pending()
    {
        var sink = new Sink();
        using var p = sink.Presenter();
        p.Receive(Msg(replayed: true, minute: 1));
        p.Receive(Msg(replayed: true, minute: 2));
        p.Receive(Msg(replayed: false, minute: 3));
        var (live, fromBacklog) = Assert.Single(sink.Shown);
        Assert.False(fromBacklog);
        Assert.Equal("Message 3", live.Title);
        Assert.Equal(2, p.Pending);
    }

    [Fact]
    public async Task Backlog_settles_on_its_own_when_hello_never_completes()
    {
        var sink = new Sink();
        using var p = sink.Presenter(settle: TimeSpan.FromMilliseconds(100));
        for (var i = 0; i < 3; i++) p.Receive(Msg(replayed: true, minute: i));
        Assert.Empty(sink.Summaries);
        await Task.Delay(600);
        Assert.Single(sink.Summaries);
        Assert.Equal(0, p.Pending);
    }

    [Fact]
    public void Summary_wording_is_neutral_and_omits_publishers_when_none_are_attributed()
    {
        var items = new[] { Msg(true, tags: ["a"], minute: 0), Msg(true, tags: ["b"], minute: 1), Msg(true, tags: ["a"], minute: 2) };
        var s = CatchUpSummary.Of(items, 0);
        Assert.Equal("3 notifications across 2 hashtags.", s.Describe());
        Assert.Empty(s.Publishers);
        Assert.Equal(3, s.Unattributed);
        Assert.DoesNotContain("agent", s.Describe(), StringComparison.OrdinalIgnoreCase);

        var mixed = CatchUpSummary.Of([Msg(true, publisher: "CI", minute: 0), Msg(true, minute: 1)], 0);
        Assert.Equal("2 notifications from 2 publishers across 1 hashtag.", mixed.Describe()); // one named, one unattributed source
        Assert.Equal(T0, mixed.OldestPublishedAtUtc);
        Assert.Equal(T0.AddMinutes(1), mixed.NewestPublishedAtUtc);
    }
}
