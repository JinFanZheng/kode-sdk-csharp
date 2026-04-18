using Kode.Agent.Sdk.Core.Events;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.Sdk.Core.Agent;

// Event subscription surface: TS-aligned Subscribe / SubscribeProgress / On
// plus the channel-list parser they share. Small leaf partial — all members
// here delegate straight through to _eventBus.
public sealed partial class Agent
{
    public sealed record SubscribeOptions
    {
        public Bookmark? Since { get; init; }
        public IReadOnlyCollection<string>? Kinds { get; init; }
    }

    /// <summary>
    /// TS-aligned: subscribe to progress/control/monitor event envelopes.
    /// Note: when <c>opts.since</c> is null, this method does NOT replay history (matches TS EventBus.subscribe()).
    /// </summary>
    public IAsyncEnumerable<EventEnvelope> Subscribe(
        IReadOnlyList<string>? channels = null,
        SubscribeOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        var flags = ParseChannels(channels);
        // TS: no replay unless `since` is explicitly provided.
        var since = opts?.Since;
        return _eventBus.SubscribeAsync(flags, since, opts?.Kinds, cancellationToken);
    }

    /// <summary>
    /// TS-aligned: subscribe to progress event envelopes only.
    /// Note: when <c>opts.since</c> is null, this method does NOT replay history.
    /// </summary>
    public IAsyncEnumerable<EventEnvelope<ProgressEvent>> SubscribeProgress(
        SubscribeOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        // TS: no replay unless `since` is explicitly provided.
        return _eventBus.SubscribeProgressAsync(opts?.Since, opts?.Kinds, cancellationToken);
    }

    /// <summary>
    /// TS-aligned: subscribe to a single control/monitor event by <c>type</c> (equivalent to TS <c>agent.on(type, handler)</c>).
    /// </summary>
    public IDisposable On(string eventType, Action<AgentEvent> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(handler);

        var channels = eventType is "permission_required" or "permission_decided"
            ? EventChannel.Control
            : EventChannel.Monitor;

        var cts = new CancellationTokenSource();
        lock (_onSubscriptionsLock)
        {
            _onSubscriptions.Add(cts);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in _eventBus.SubscribeAsync(
                    channels,
                    since: null,
                    kinds: new[] { eventType },
                    cancellationToken: cts.Token))
                {
                    handler(envelope.Event);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on dispose
            }
            catch (ObjectDisposedException)
            {
                // agent shutting down
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unhandled exception in Agent.On subscription loop for {EventType}", eventType);
            }
        }, cts.Token);

        return new CancellationDisposable(cts, this);
    }

    private static EventChannel ParseChannels(IReadOnlyList<string>? channels)
    {
        if (channels == null || channels.Count == 0) return EventChannel.All;

        var flags = (EventChannel)0;
        foreach (var c in channels)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var norm = c.Trim().ToLowerInvariant();
            flags |= norm switch
            {
                "progress" => EventChannel.Progress,
                "control" => EventChannel.Control,
                "monitor" => EventChannel.Monitor,
                "all" => EventChannel.All,
                _ => throw new ArgumentException($"Unknown channel: {c}", nameof(channels))
            };
        }

        return flags == 0 ? EventChannel.All : flags;
    }
}
