using HashRelay.Contracts;

namespace HashRelay.Client;

/// <summary>
/// Decides how a received delivery is presented, so that availability of every message never means a toast for every
/// message. Live deliveries are shown at once. Deliveries flagged <see cref="NotificationMessage.Replayed"/> are the
/// backlog the server hands over during <c>Hello</c>: they are collected and, once the batch settles (Hello completed,
/// or nothing more arrived for <see cref="Settle"/>), presented as one catch-up summary. High-priority backlog items
/// still get their own toast, newest first, but no more than <see cref="MaxUrgentToasts"/> of them; a lone backlog item
/// is simply shown. Duplicates (a replay racing a live send, a resend of an unacknowledged delivery) are dropped by
/// notification id. Presentation is separate from acknowledgement: callers ack every delivery they receive, shown or not.
/// </summary>
public sealed class NotificationPresenter : IDisposable
{
    public const int DefaultMaxUrgentToasts = 3;
    public const int RememberedIds = 200;
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromSeconds(2);

    private readonly Action<NotificationMessage, bool> _show;
    private readonly Action<CatchUpSummary> _summarize;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _seen = [];
    private readonly Queue<Guid> _seenOrder = new();
    private readonly List<NotificationMessage> _backlog = [];
    private readonly Timer? _settleTimer;
    private bool _disposed;

    /// <param name="show">Shows one notification; the flag is true when it comes from the backlog rather than a live publication.</param>
    /// <param name="summarize">Shows the catch-up summary for a backlog batch of two or more.</param>
    /// <param name="maxUrgentToasts">How many high-priority backlog items may be shown individually per batch.</param>
    /// <param name="settle">Quiet time after the last backlog message before the batch is flushed without waiting for <see cref="Flush"/>; <see cref="Timeout.InfiniteTimeSpan"/> disables the timer.</param>
    public NotificationPresenter(Action<NotificationMessage, bool> show, Action<CatchUpSummary> summarize, int maxUrgentToasts = DefaultMaxUrgentToasts, TimeSpan? settle = null)
    {
        _show = show;
        _summarize = summarize;
        MaxUrgentToasts = Math.Max(0, maxUrgentToasts);
        Settle = settle ?? DefaultSettle;
        if (Settle != Timeout.InfiniteTimeSpan) _settleTimer = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public int MaxUrgentToasts { get; }
    public TimeSpan Settle { get; }
    /// <summary>Backlog received and not yet presented.</summary>
    public int Pending { get { lock (_gate) return _backlog.Count; } }

    /// <summary>Routes one received delivery. Returns false when it was a duplicate of something already handled.</summary>
    public bool Receive(NotificationMessage m)
    {
        lock (_gate)
        {
            if (_disposed || !Remember(m.NotificationId)) return false;
            if (m.Replayed)
            {
                _backlog.Add(m);
                _settleTimer?.Change(Settle, Timeout.InfiniteTimeSpan);
                return true;
            }
        }
        _show(m, false);
        return true;
    }

    /// <summary>Presents the collected backlog. Call when Hello completes; harmless when there is nothing pending.</summary>
    public void Flush()
    {
        List<NotificationMessage> items;
        lock (_gate)
        {
            if (_backlog.Count == 0) return;
            items = _backlog.OrderBy(m => m.PublishedAtUtc).ToList();
            _backlog.Clear();
            _settleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        var urgent = items.Where(m => m.Priority == Priorities.High).OrderByDescending(m => m.PublishedAtUtc).Take(MaxUrgentToasts)
            .Select(m => m.DeliveryId).ToHashSet();
        var rest = items.Where(m => !urgent.Contains(m.DeliveryId)).ToList();
        foreach (var m in items.Where(m => urgent.Contains(m.DeliveryId))) _show(m, true);
        if (rest.Count == 1) _show(rest[0], true);
        else if (rest.Count > 1) _summarize(CatchUpSummary.Of(rest, urgent.Count));
    }

    private bool Remember(Guid id)
    {
        if (!_seen.Add(id)) return false;
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > RememberedIds) _seen.Remove(_seenOrder.Dequeue());
        return true;
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _settleTimer?.Dispose();
    }
}

/// <summary>One catch-up batch: what arrived while the device was away, minus any urgent items shown on their own.</summary>
/// <param name="Items">The summarised deliveries, oldest first.</param>
/// <param name="UrgentShown">High-priority items from the same batch that were presented individually.</param>
public sealed record CatchUpSummary(IReadOnlyList<NotificationMessage> Items, int UrgentShown)
{
    public int Count => Items.Count;
    public int Total => Items.Count + UrgentShown;
    public IReadOnlyList<string> Hashtags => Items.SelectMany(m => m.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
    /// <summary>Distinct server-verified publisher names; legacy or unattributed items are not counted here.</summary>
    public IReadOnlyList<string> Publishers => Items.Where(m => m.Publisher?.IsKnown == true).Select(m => m.Publisher!.Name!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    public int Unattributed => Items.Count(m => m.Publisher?.IsKnown != true);
    public DateTimeOffset OldestPublishedAtUtc => Items.Min(m => m.PublishedAtUtc);
    public DateTimeOffset NewestPublishedAtUtc => Items.Max(m => m.PublishedAtUtc);

    public static CatchUpSummary Of(IEnumerable<NotificationMessage> items, int urgentShown)
        => new(items.OrderBy(m => m.PublishedAtUtc).ToList(), urgentShown);

    /// <summary>Headline for the summary toast, e.g. "47 notifications while you were away".</summary>
    public string Title => $"{Total} notifications while you were away";

    /// <summary>
    /// Neutral one-line description, e.g. "47 notifications from 4 publishers across 3 hashtags." Publishers are counted
    /// only when the server attributed them; nothing here calls anything an agent unless it was registered as one.
    /// </summary>
    public string Describe()
    {
        var publishers = Publishers.Count + (Unattributed > 0 ? 1 : 0); // unattributed items count as one unnamed source
        var tags = Hashtags.Count;
        var text = $"{Plural(Count, "notification")}";
        if (Publishers.Count > 0) text += $" from {Plural(publishers, "publisher")}";
        text += $" across {Plural(tags, "hashtag")}.";
        if (UrgentShown > 0) text += $" {Plural(UrgentShown, "high-priority notification")} shown separately.";
        return text;
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
