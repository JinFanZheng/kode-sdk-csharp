using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.Sdk.Core.Events;

/// <summary>
/// Configuration for <see cref="EventBus"/>.
/// </summary>
public sealed record EventBusOptions
{
    /// <summary>
    /// Per-subscriber bounded channel capacity. When full, the oldest unread event for that
    /// subscriber is dropped (never blocks the agent). Increase for UI consumers that may
    /// temporarily fall behind during bursty token streaming; decrease to bound memory.
    /// Default: 1000.
    /// </summary>
    public int SubscriberChannelCapacity { get; init; } = 1000;
}

/// <summary>
/// Implementation of the three-channel event bus.
/// </summary>
public sealed class EventBus : IEventBus, IAsyncDisposable
{
    private readonly IAgentStore? _store;
    private readonly string? _agentId;
    private readonly ILogger<EventBus>? _logger;
    private readonly EventBusOptions _options;
    private long _droppedEventsCount;

    // TS-aligned: buffer critical events when persistence fails.
    private readonly List<Timeline> _failedEvents = [];
    private const int MaxFailedBuffer = 1000;
    private readonly System.Threading.Lock _failedLock = new();
    private int _retryingFailedEvents;

    private long _cursor;
    private long _seq;
    private Bookmark? _lastBookmark;
    private readonly System.Threading.Lock _lock = new();

    // In-memory timeline for replay
    private readonly List<Timeline> _timeline = [];

    // Broadcast channels for async streaming (multi-subscriber safe).
    private readonly Dictionary<long, Channel<EventEnvelope>> _allSubscribers = [];
    private readonly Dictionary<long, Channel<EventEnvelope>> _progressSubscribers = [];
    private long _subscriberId;
    private readonly System.Threading.Lock _subLock = new();
    private bool _completed;

    // Event handlers for control/monitor
    private readonly Dictionary<Type, List<Delegate>> _controlHandlers = [];
    private readonly Dictionary<Type, List<Delegate>> _monitorHandlers = [];

    public EventBus(IAgentStore? store = null, string? agentId = null, ILogger<EventBus>? logger = null)
        : this(store, agentId, logger, options: null)
    {
    }

    public EventBus(
        IAgentStore? store,
        string? agentId,
        ILogger<EventBus>? logger,
        EventBusOptions? options)
    {
        _store = store;
        _agentId = agentId;
        _logger = logger;
        _options = options ?? new EventBusOptions();
        if (_options.SubscriberChannelCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "EventBusOptions.SubscriberChannelCapacity must be positive.");
        }
    }

    /// <summary>
    /// Total number of events dropped across all subscribers since this EventBus was created.
    /// Each drop corresponds to a slow subscriber whose bounded channel reached capacity, at
    /// which point the oldest unread event for that subscriber is discarded so emission never
    /// blocks. Use for observability (a rising count signals a lagging consumer).
    /// </summary>
    public long GetDroppedEventCount() => Interlocked.Read(ref _droppedEventsCount);

    public Bookmark? GetLastBookmark() => LastBookmark;

    public int GetFailedEventCount() => GetFailedCount();

    public async Task FlushFailedEventsAsync(CancellationToken cancellationToken = default)
    {
        while (GetFailedCount() > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RetryFailedEventsAsync();
            if (GetFailedCount() > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    public long GetCursor()
    {
        lock (_lock)
        {
            return _cursor;
        }
    }

    /// <summary>
    /// Seeds the internal cursor/sequence from a previously persisted bookmark (for resume).
    /// </summary>
    public void SeedFromBookmark(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        lock (_lock)
        {
            // TS-aligned counters: next seq/cursor should be lastSeq+1.
            _seq = Math.Max(_seq, bookmark.Seq + 1);
            _cursor = Math.Max(_cursor, bookmark.Seq + 1);
            _lastBookmark = bookmark;
        }
    }

    /// <inheritdoc />
    public EventEnvelope<TEvent> EmitProgress<TEvent>(TEvent @event) where TEvent : ProgressEvent
    {
        var envelope = CreateEnvelope(@event, EventChannel.Progress);
        WriteToChannels(envelope);
        Persist(envelope);
        var stamped = (TEvent)envelope.Event;
        return new EventEnvelope<TEvent>
        {
            Cursor = envelope.Cursor,
            Bookmark = envelope.Bookmark,
            Event = stamped
        };
    }

    /// <inheritdoc />
    public EventEnvelope<TEvent> EmitControl<TEvent>(TEvent @event) where TEvent : ControlEvent
    {
        var envelope = CreateEnvelope(@event, EventChannel.Control);
        WriteToChannels(envelope);
        var stamped = (TEvent)envelope.Event;
        InvokeHandlers(_controlHandlers, stamped);
        Persist(envelope);
        return new EventEnvelope<TEvent>
        {
            Cursor = envelope.Cursor,
            Bookmark = envelope.Bookmark,
            Event = stamped
        };
    }

    /// <inheritdoc />
    public EventEnvelope<TEvent> EmitMonitor<TEvent>(TEvent @event) where TEvent : MonitorEvent
    {
        var envelope = CreateEnvelope(@event, EventChannel.Monitor);
        WriteToChannels(envelope);
        var stamped = (TEvent)envelope.Event;
        InvokeHandlers(_monitorHandlers, stamped);
        Persist(envelope);
        return new EventEnvelope<TEvent>
        {
            Cursor = envelope.Cursor,
            Bookmark = envelope.Bookmark,
            Event = stamped
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EventEnvelope> SubscribeAsync(
        EventChannel channels = EventChannel.All,
        Bookmark? since = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var envelope in SubscribeAsync(channels, since, kinds: null, cancellationToken))
        {
            yield return envelope;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EventEnvelope> SubscribeAsync(
        EventChannel channels,
        Bookmark? since,
        IReadOnlyCollection<string>? kinds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (subscriptionId, channel) = CreateSubscription(progressOnly: false);
        // TS-aligned: when since is null, do NOT replay history; only receive newly published events.
        var lastSeq = since?.Seq ?? -1L;
        var kindSet = kinds is { Count: > 0 }
            ? new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase)
            : null;

        // First yield historical events only when since is provided (TS behavior).
        var earliestInMemorySeq = GetEarliestInMemorySeq();
        try
        {
            if (since != null && TryShouldReadHistoryFromStore(since, earliestInMemorySeq))
            {
                var storeChannel = GetStoreChannelFilter(channels);
                await foreach (var item in _store!.ReadEventsAsync(_agentId!, storeChannel, since, cancellationToken))
                {
                    if ((ToEventChannelFlag(item.Event.Channel) & channels) == 0) continue;
                    if (item.Bookmark.Seq >= earliestInMemorySeq) continue; // avoid duplicating in-memory events
                    var envelope = ToEnvelope(item);
                    if (kindSet != null && !kindSet.Contains(envelope.Event.Type)) continue;
                    lastSeq = Math.Max(lastSeq, envelope.Bookmark.Seq);
                    yield return envelope;
                }
            }
            else if (since != null)
            {
                foreach (var envelope in GetHistoricalEvents(since, channels))
                {
                    if (kindSet != null && !kindSet.Contains(envelope.Event.Type)) continue;
                    lastSeq = Math.Max(lastSeq, envelope.Bookmark.Seq);
                    yield return envelope;
                }
            }

            // Then yield new events (broadcast; filter + de-dupe by seq).
            await foreach (var envelope in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if ((ToEventChannelFlag(envelope.Event.Channel) & channels) == 0) continue;
                if (envelope.Bookmark.Seq <= lastSeq) continue;
                if (kindSet != null && !kindSet.Contains(envelope.Event.Type)) continue;
                lastSeq = envelope.Bookmark.Seq;
                yield return envelope;
            }
        }
        finally
        {
            RemoveSubscription(subscriptionId, progressOnly: false);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EventEnvelope<ProgressEvent>> SubscribeProgressAsync(
        Bookmark? since = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var envelope in SubscribeProgressAsync(since, kinds: null, cancellationToken))
        {
            yield return envelope;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EventEnvelope<ProgressEvent>> SubscribeProgressAsync(
        Bookmark? since,
        IReadOnlyCollection<string>? kinds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (subscriptionId, channel) = CreateSubscription(progressOnly: true);
        // TS-aligned: when since is null, do NOT replay history; only receive newly published events.
        var lastSeq = since?.Seq ?? -1L;
        var kindSet = kinds is { Count: > 0 }
            ? new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase)
            : null;

        // First yield historical events only when since is provided (TS behavior).
        var earliestInMemorySeq = GetEarliestInMemorySeq();
        try
        {
            if (since != null && TryShouldReadHistoryFromStore(since, earliestInMemorySeq))
            {
                await foreach (var item in _store!.ReadEventsAsync(_agentId!, EventChannel.Progress, since, cancellationToken))
                {
                    if (item.Bookmark.Seq >= earliestInMemorySeq) continue; // avoid duplicating in-memory events
                    var envelope = ToEnvelope(item);
                    if (kindSet != null && !kindSet.Contains(envelope.Event.Type)) continue;
                    lastSeq = Math.Max(lastSeq, envelope.Bookmark.Seq);
                    yield return new EventEnvelope<ProgressEvent>
                    {
                        Cursor = envelope.Cursor,
                        Bookmark = envelope.Bookmark,
                        Event = (ProgressEvent)envelope.Event
                    };
                }
            }
            else if (since != null)
            {
                foreach (var envelope in GetHistoricalEvents(since, EventChannel.Progress))
                {
                    if (kindSet != null && !kindSet.Contains(envelope.Event.Type)) continue;
                    lastSeq = Math.Max(lastSeq, envelope.Bookmark.Seq);
                    yield return new EventEnvelope<ProgressEvent>
                    {
                        Cursor = envelope.Cursor,
                        Bookmark = envelope.Bookmark,
                        Event = (ProgressEvent)envelope.Event
                    };
                }
            }

            // Then yield new progress events (broadcast; de-dupe by seq).
            await foreach (var envelope in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (envelope.Bookmark.Seq <= lastSeq) continue;
                if (kindSet != null && !kindSet.Contains(envelope.Event.Type)) continue;
                lastSeq = envelope.Bookmark.Seq;
                yield return new EventEnvelope<ProgressEvent>
                {
                    Cursor = envelope.Cursor,
                    Bookmark = envelope.Bookmark,
                    Event = (ProgressEvent)envelope.Event
                };
            }
        }
        finally
        {
            RemoveSubscription(subscriptionId, progressOnly: true);
        }
    }

    /// <inheritdoc />
    public IDisposable OnControl<TEvent>(Action<TEvent> handler) where TEvent : ControlEvent
    {
        return RegisterHandler(_controlHandlers, handler);
    }

    /// <inheritdoc />
    public IDisposable OnMonitor<TEvent>(Action<TEvent> handler) where TEvent : MonitorEvent
    {
        return RegisterHandler(_monitorHandlers, handler);
    }

    /// <summary>
    /// Completes the event channels.
    /// </summary>
    public void Complete()
    {
        List<Channel<EventEnvelope>> all;
        List<Channel<EventEnvelope>> progress;
        lock (_subLock)
        {
            if (_completed) return;
            _completed = true;
            all = _allSubscribers.Values.ToList();
            progress = _progressSubscribers.Values.ToList();
            _allSubscribers.Clear();
            _progressSubscribers.Clear();
        }

        foreach (var ch in all)
        {
            ch.Writer.TryComplete();
        }
        foreach (var ch in progress)
        {
            ch.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Gets a snapshot of the current in-memory timeline (for context compression/history).
    /// </summary>
    public IReadOnlyList<Timeline> GetTimelineSnapshot()
    {
        lock (_lock)
        {
            return _timeline.ToList();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }

    private static string ToAgentChannel(EventChannel channel) =>
        channel switch
        {
            EventChannel.Progress => "progress",
            EventChannel.Control => "control",
            EventChannel.Monitor => "monitor",
            _ => "monitor"
        };

    private static EventChannel ToEventChannelFlag(string? channel) =>
        channel?.ToLowerInvariant() switch
        {
            "progress" => EventChannel.Progress,
            "control" => EventChannel.Control,
            "monitor" => EventChannel.Monitor,
            _ => EventChannel.All
        };

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private EventEnvelope CreateEnvelope(AgentEvent @event, EventChannel channel)
    {
        long cursor, seq;
        lock (_lock)
        {
            // TS-aligned: cursor/seq use post-increment (first event is 0).
            cursor = _cursor++;
            seq = _seq++;
        }

        var bookmark = new Bookmark { Seq = seq, Timestamp = NowMs() };
        var stampedEvent = @event with { Channel = ToAgentChannel(channel), Bookmark = bookmark };

        var envelope = new EventEnvelope
        {
            Cursor = cursor,
            Bookmark = bookmark,
            Event = stampedEvent
        };

        lock (_lock)
        {
            _timeline.Add(new Timeline
            {
                Cursor = cursor,
                Bookmark = bookmark,
                Event = stampedEvent
            });
            if (_timeline.Count > 10000)
            {
                _timeline.RemoveRange(0, _timeline.Count - 5000);
            }
            _lastBookmark = envelope.Bookmark;
        }

        return envelope;
    }

    /// <summary>
    /// Gets the most recent bookmark emitted by this bus (if any).
    /// </summary>
    public Bookmark? LastBookmark
    {
        get
        {
            lock (_lock)
            {
                return _lastBookmark;
            }
        }
    }

    private void WriteToChannels(EventEnvelope envelope)
    {
        Channel<EventEnvelope>[] all;
        Channel<EventEnvelope>[] progress;
        var isProgress = string.Equals(envelope.Event.Channel, "progress", StringComparison.OrdinalIgnoreCase);
        lock (_subLock)
        {
            if (_completed) return;
            all = _allSubscribers.Values.ToArray();
            progress = isProgress
                ? _progressSubscribers.Values.ToArray()
                : [];
        }

        foreach (var ch in all)
        {
            ch.Writer.TryWrite(envelope);
        }

        foreach (var ch in progress)
        {
            ch.Writer.TryWrite(envelope);
        }
    }

    private (long SubscriptionId, Channel<EventEnvelope> Channel) CreateSubscription(bool progressOnly)
    {
        var options = new BoundedChannelOptions(_options.SubscriberChannelCapacity)
        {
            // Never let a slow subscriber block the agent; drop old events for that subscriber.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        };

        var channel = Channel.CreateBounded<EventEnvelope>(options, OnItemDropped);

        lock (_subLock)
        {
            if (_completed)
            {
                channel.Writer.TryComplete();
                throw new ObjectDisposedException(nameof(EventBus));
            }

            var id = ++_subscriberId;
            if (progressOnly)
            {
                _progressSubscribers[id] = channel;
            }
            else
            {
                _allSubscribers[id] = channel;
            }
            return (id, channel);
        }
    }

    private void OnItemDropped(EventEnvelope dropped)
    {
        Interlocked.Increment(ref _droppedEventsCount);
    }

    private void RemoveSubscription(long subscriptionId, bool progressOnly)
    {
        Channel<EventEnvelope>? channel = null;
        lock (_subLock)
        {
            if (progressOnly)
            {
                if (_progressSubscribers.Remove(subscriptionId, out var ch))
                {
                    channel = ch;
                }
            }
            else
            {
                if (_allSubscribers.Remove(subscriptionId, out var ch))
                {
                    channel = ch;
                }
            }
        }
        channel?.Writer.TryComplete();
    }

    private void InvokeHandlers<TEvent>(Dictionary<Type, List<Delegate>> handlers, TEvent @event) where TEvent : AgentEvent
    {
        var type = typeof(TEvent);
        lock (handlers)
        {
            if (handlers.TryGetValue(type, out var list))
            {
                foreach (var handler in list.ToArray())
                {
                    try
                    {
                        ((Action<TEvent>)handler)(@event);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error invoking event handler for {EventType}", type.Name);
                    }
                }
            }
        }
    }

    private IDisposable RegisterHandler<TEvent>(Dictionary<Type, List<Delegate>> handlers, Action<TEvent> handler)
    {
        var type = typeof(TEvent);
        lock (handlers)
        {
            if (!handlers.TryGetValue(type, out var list))
            {
                list = [];
                handlers[type] = list;
            }
            list.Add(handler);
        }

        return new HandlerDisposer(() =>
        {
            lock (handlers)
            {
                if (handlers.TryGetValue(type, out var list))
                {
                    list.Remove(handler);
                }
            }
        });
    }

    private IEnumerable<EventEnvelope> GetHistoricalEvents(Bookmark? since, EventChannel channels)
    {
        // Snapshot under lock, yield outside — avoids holding the lock across the iterator's
        // `yield` boundary (required by System.Threading.Lock and generally safer for async consumers).
        Timeline[] snapshot;
        lock (_lock)
        {
            snapshot = _timeline.ToArray();
        }

        foreach (var item in snapshot)
        {
            if (since != null && item.Bookmark.Seq <= since.Seq)
            {
                continue;
            }

            if ((ToEventChannelFlag(item.Event.Channel) & channels) != 0)
            {
                yield return new EventEnvelope
                {
                    Cursor = item.Cursor,
                    Bookmark = item.Bookmark,
                    Event = item.Event
                };
            }
        }
    }

    private bool TryShouldReadHistoryFromStore(Bookmark? since)
    {
        return TryShouldReadHistoryFromStore(since, GetEarliestInMemorySeq());
    }

    private bool TryShouldReadHistoryFromStore(Bookmark? since, long earliestInMemorySeq)
    {
        if (_store == null || _agentId == null) return false;
        if (since == null) return false;
        // If the caller asks for a bookmark older than what this instance has in memory, read from store.
        return since.Seq < earliestInMemorySeq;
    }

    private long GetEarliestInMemorySeq()
    {
        lock (_lock)
        {
            return _timeline.Count > 0 ? _timeline[0].Bookmark.Seq : long.MaxValue;
        }
    }

    private static EventChannel? GetStoreChannelFilter(EventChannel channels)
    {
        // Store accepts single channel filters; for multi-channel requests, use null (read all and filter in-memory).
        var isSingle = channels is EventChannel.Progress or EventChannel.Control or EventChannel.Monitor;
        return isSingle ? channels : null;
    }

    private static EventEnvelope ToEnvelope(Timeline timeline)
    {
        var bookmark = timeline.Bookmark;
        var ev = timeline.Event.Bookmark == null ? timeline.Event with { Bookmark = bookmark } : timeline.Event;
        return new EventEnvelope
        {
            Cursor = timeline.Cursor,
            Bookmark = bookmark,
            Event = ev
        };
    }

    private void Persist(EventEnvelope envelope)
    {
        if (_store == null || _agentId == null) return;

        var timeline = new Timeline
        {
            Cursor = envelope.Cursor,
            Bookmark = envelope.Bookmark,
            Event = envelope.Event
        };

        // Fire and forget, but log errors
        _ = PersistAsync(timeline);
    }

    private static readonly HashSet<string> CriticalTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tool:end",
        "done",
        "permission_decided",
        "agent_resumed",
        "state_changed",
        "breakpoint_changed",
        "error"
    };

    private static bool IsCriticalEvent(Timeline timeline) => CriticalTypes.Contains(timeline.Event.Type);

    private async Task PersistAsync(Timeline timeline)
    {
        try
        {
            await _store!.AppendEventAsync(_agentId!, timeline);

            // TS-aligned: after a successful persist, try to retry previously failed critical events.
            if (GetFailedCount() > 0)
            {
                TryStartRetryFailedEvents();
            }
        }
        catch (Exception ex)
        {
            if (IsCriticalEvent(timeline))
            {
                BufferFailedCriticalEvent(timeline);
                EmitStorageFailureDegraded(timeline, ex);
            }
            else
            {
                _logger?.LogWarning(ex, "Failed to persist non-critical event {EventType}", timeline.Event.Type);
            }
        }
    }

    private int GetFailedCount()
    {
        lock (_failedLock)
        {
            return _failedEvents.Count;
        }
    }

    private void BufferFailedCriticalEvent(Timeline timeline)
    {
        lock (_failedLock)
        {
            _failedEvents.Add(timeline);
            if (_failedEvents.Count > MaxFailedBuffer)
            {
                _failedEvents.RemoveRange(0, _failedEvents.Count - MaxFailedBuffer);
            }
        }
    }

    private void EmitStorageFailureDegraded(Timeline failedTimeline, Exception ex)
    {
        // TS-aligned: emit a degraded monitor event that is NOT persisted.
        try
        {
            var buffered = GetFailedCount();
            InvokeHandlers(_monitorHandlers, new StorageFailureEvent
            {
                Type = "storage_failure",
                Severity = "critical",
                FailedEvent = failedTimeline.Event.Type,
                BufferedCount = buffered,
                Error = ex.Message
            });
        }
        catch
        {
            // best-effort; never block event emission
        }
    }

    private void TryStartRetryFailedEvents()
    {
        if (_store == null || _agentId == null) return;
        if (Interlocked.CompareExchange(ref _retryingFailedEvents, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await RetryFailedEventsAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to retry buffered events");
            }
            finally
            {
                Interlocked.Exchange(ref _retryingFailedEvents, 0);
            }
        });
    }

    private async Task RetryFailedEventsAsync()
    {
        if (_store == null || _agentId == null) return;
        var agentId = _agentId;

        List<Timeline> toRetry;
        lock (_failedLock)
        {
            if (_failedEvents.Count == 0) return;
            var take = Math.Min(10, _failedEvents.Count);
            toRetry = _failedEvents.GetRange(0, take);
            _failedEvents.RemoveRange(0, take);
        }

        for (var i = 0; i < toRetry.Count; i++)
        {
            var item = toRetry[i];
            try
            {
                await _store.AppendEventAsync(agentId, item);
            }
            catch
            {
                // Put the failed + remaining events back (front) to preserve retry order.
                lock (_failedLock)
                {
                    _failedEvents.InsertRange(0, toRetry.Skip(i).ToList());
                }
                break;
            }
        }
    }

    private sealed class HandlerDisposer(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            dispose();
        }
    }
}
