using System.Runtime.CompilerServices;
using System.Threading.Channels;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;

namespace KodaClaw.ControlPlane;

public sealed class InMemoryDiagnosticsService : IDiagnosticsService, IDisposable
{
    private const int HighPriorityMinCapacity = 200;
    private const int MaxTotalCapacity = 1000;
    private readonly object _gate = new();
    private readonly List<DiagnosticEvent> _highPriorityCache = [];
    private readonly List<DiagnosticEvent> _lowPriorityCache = [];
    private readonly List<Channel<DiagnosticEvent>> _subscribers = [];

    public IReadOnlyList<DiagnosticEvent> GetRecent(int limit = 50, string? correlationId = null)
    {
        return Query(new DiagnosticsQuery(Limit: limit, CorrelationId: correlationId));
    }

    public IReadOnlyList<DiagnosticEvent> Query(DiagnosticsQuery? query = null)
    {
        var effective = query ?? new DiagnosticsQuery();
        if (effective.Limit <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            IEnumerable<DiagnosticEvent> filtered = _highPriorityCache.Concat(_lowPriorityCache);
            if (!string.IsNullOrWhiteSpace(effective.CorrelationId))
            {
                filtered = filtered.Where(item =>
                    string.Equals(item.CorrelationId, effective.CorrelationId, StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(effective.SessionId))
            {
                filtered = filtered.Where(item =>
                    string.Equals(item.SessionId, effective.SessionId, StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(effective.Source))
            {
                filtered = filtered.Where(item =>
                    string.Equals(item.Source, effective.Source, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(effective.EventType))
            {
                filtered = filtered.Where(item =>
                    string.Equals(item.EventType, effective.EventType, StringComparison.OrdinalIgnoreCase));
            }

            if (effective.Levels is { Length: > 0 })
            {
                filtered = filtered.Where(item =>
                    effective.Levels.Any(l =>
                        string.Equals(item.Level, l, StringComparison.OrdinalIgnoreCase)));
            }

            return filtered
                .OrderByDescending(item => item.Timestamp)
                .Take(effective.Limit)
                .ToArray();
        }
    }

    public void Record(DiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        lock (_gate)
        {
            if (IsHighPriority(diagnosticEvent.Level))
            {
                _highPriorityCache.Add(diagnosticEvent);
            }
            else
            {
                _lowPriorityCache.Add(diagnosticEvent);
            }

            var totalCount = _highPriorityCache.Count + _lowPriorityCache.Count;
            if (totalCount > MaxTotalCapacity)
            {
                if (_lowPriorityCache.Count > 0)
                {
                    var toRemove = totalCount - MaxTotalCapacity;
                    var removeFromLow = Math.Min(toRemove, _lowPriorityCache.Count);
                    _lowPriorityCache.RemoveRange(0, removeFromLow);
                }
                else if (_highPriorityCache.Count > HighPriorityMinCapacity)
                {
                    var toRemove = _highPriorityCache.Count - HighPriorityMinCapacity;
                    _highPriorityCache.RemoveRange(0, toRemove);
                }
            }
            else if (_lowPriorityCache.Count == 0 && _highPriorityCache.Count > HighPriorityMinCapacity)
            {
                var toRemove = _highPriorityCache.Count - HighPriorityMinCapacity;
                _highPriorityCache.RemoveRange(0, toRemove);
            }

            foreach (var ch in _subscribers)
            {
                ch.Writer.TryWrite(diagnosticEvent);
            }
        }
    }

    public DiagnosticsStatsResponse GetStats(DateTimeOffset? since = null)
    {
        lock (_gate)
        {
            IEnumerable<DiagnosticEvent> combined = _highPriorityCache.Concat(_lowPriorityCache);
            var source = since.HasValue
                ? combined.Where(e => e.Timestamp >= since.Value).ToArray()
                : combined.ToArray();

            if (source.Length == 0)
            {
                return new DiagnosticsStatsResponse(0, 0, 0, [], null, null);
            }

            var bySource = source
                .GroupBy(e => e.Source, StringComparer.OrdinalIgnoreCase)
                .Select(g => new DiagnosticsSourceStats(
                    Source: g.Key,
                    Count: g.Count(),
                    ErrorCount: g.Count(e => string.Equals(e.Level, "error", StringComparison.OrdinalIgnoreCase)),
                    WarningCount: g.Count(e => string.Equals(e.Level, "warning", StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(s => s.Count)
                .ToArray();

            return new DiagnosticsStatsResponse(
                TotalEvents: source.Length,
                ErrorCount: source.Count(e => string.Equals(e.Level, "error", StringComparison.OrdinalIgnoreCase)),
                WarningCount: source.Count(e => string.Equals(e.Level, "warning", StringComparison.OrdinalIgnoreCase)),
                BySource: bySource,
                OldestEvent: source.Min(e => e.Timestamp),
                NewestEvent: source.Max(e => e.Timestamp));
        }
    }

    public Task ClearAsync(DateTimeOffset? before = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (before.HasValue)
            {
                _highPriorityCache.RemoveAll(e => e.Timestamp < before.Value);
                _lowPriorityCache.RemoveAll(e => e.Timestamp < before.Value);
            }
            else
            {
                _highPriorityCache.Clear();
                _lowPriorityCache.Clear();
            }
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<DiagnosticEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<DiagnosticEvent>(
            new UnboundedChannelOptions { SingleReader = true });

        lock (_gate)
        {
            _subscribers.Add(channel);
        }

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return evt;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }

            channel.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var ch in _subscribers)
            {
                ch.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }

    private static bool IsHighPriority(string? level)
    {
        return string.Equals(level, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(level, "critical", StringComparison.OrdinalIgnoreCase)
            || string.Equals(level, "warning", StringComparison.OrdinalIgnoreCase);
    }
}
