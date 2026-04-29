using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Workspace;

namespace KodaClaw.ControlPlane;

public sealed class FileDiagnosticsService : IDiagnosticsService, IDisposable
{
    private const int HighPriorityMinCapacity = 200;
    private const int MaxTotalCapacity = 1000;
    private const int ReloadLimit = 500;
    private const long MaxJournalSizeBytes = 50 * 1024 * 1024;
    private const int RetentionDays = 7;
    private const int RotationCheckIntervalSeconds = 300;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _journalPath;
    private readonly Lock _gate = new();
    private readonly List<DiagnosticEvent> _highPriorityCache = [];
    private readonly List<DiagnosticEvent> _lowPriorityCache = [];
    private readonly List<Channel<DiagnosticEvent>> _subscribers = [];
    private readonly Timer? _rotationTimer;
    private int _rotating = 0;

    public FileDiagnosticsService(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _journalPath = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.DiagnosticsJournalFile);
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        LoadFromFile();
        _rotationTimer = new Timer(_ => RotateJournal(), null, TimeSpan.FromSeconds(RotationCheckIntervalSeconds), TimeSpan.FromSeconds(RotationCheckIntervalSeconds));
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

            AppendToFile(diagnosticEvent);

            foreach (var ch in _subscribers)
            {
                ch.Writer.TryWrite(diagnosticEvent);
            }
        }
    }

    public IReadOnlyList<DiagnosticEvent> GetRecent(int limit = 50, string? correlationId = null)
    {
        return Query(new DiagnosticsQuery(Limit: limit, CorrelationId: correlationId));
    }

    public IReadOnlyList<DiagnosticEvent> Query(DiagnosticsQuery? query = null)
    {
        var q = query ?? new DiagnosticsQuery();
        if (q.Limit <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            IEnumerable<DiagnosticEvent> filtered = _highPriorityCache.Concat(_lowPriorityCache);

            if (!string.IsNullOrWhiteSpace(q.CorrelationId))
            {
                filtered = filtered.Where(e => string.Equals(e.CorrelationId, q.CorrelationId, StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(q.SessionId))
            {
                filtered = filtered.Where(e => string.Equals(e.SessionId, q.SessionId, StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(q.Source))
            {
                filtered = filtered.Where(e => string.Equals(e.Source, q.Source, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(q.EventType))
            {
                filtered = filtered.Where(e => string.Equals(e.EventType, q.EventType, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(q.Level))
            {
                filtered = filtered.Where(e => string.Equals(e.Level, q.Level, StringComparison.OrdinalIgnoreCase));
            }

            if (q.DateFrom.HasValue)
            {
                filtered = filtered.Where(e => e.Timestamp >= q.DateFrom.Value);
            }

            if (q.DateTo.HasValue)
            {
                filtered = filtered.Where(e => e.Timestamp <= q.DateTo.Value);
            }

            var memoryResults = filtered
                .OrderByDescending(e => e.Timestamp)
                .Take(q.Limit)
                .ToList();

            var hasAnyCache = _highPriorityCache.Count > 0 || _lowPriorityCache.Count > 0;
            if (q.DateFrom.HasValue && hasAnyCache)
            {
                var oldestHigh = _highPriorityCache.Count > 0
                    ? _highPriorityCache.Min(e => e.Timestamp)
                    : DateTimeOffset.MaxValue;
                var oldestLow = _lowPriorityCache.Count > 0
                    ? _lowPriorityCache.Min(e => e.Timestamp)
                    : DateTimeOffset.MaxValue;
                var oldestInCache = oldestHigh < oldestLow ? oldestHigh : oldestLow;
                if (oldestInCache > q.DateFrom.Value)
                {
                    var fileResults = ReadFromFile(q.DateFrom.Value, oldestInCache, q.Limit);
                    return memoryResults
                        .Concat(fileResults)
                        .OrderByDescending(e => e.Timestamp)
                        .Take(q.Limit)
                        .ToArray();
                }
            }

            return memoryResults.ToArray();
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

    public async Task ClearAsync(DateTimeOffset? before = null, CancellationToken cancellationToken = default)
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

        await RewriteFileAsync(before, cancellationToken);
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
        _rotationTimer?.Dispose();

        lock (_gate)
        {
            foreach (var ch in _subscribers)
            {
                ch.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }

    private void RotateJournal()
    {
        if (Interlocked.CompareExchange(ref _rotating, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!File.Exists(_journalPath))
            {
                return;
            }

            var fileInfo = new FileInfo(_journalPath);
            if (fileInfo.Length <= MaxJournalSizeBytes)
            {
                return;
            }

            var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
            var allLines = File.ReadAllLines(_journalPath);
            var keep = new List<string>();
            var archive = new List<string>();

            foreach (var line in allLines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var evt = JsonSerializer.Deserialize<DiagnosticEvent>(line, JsonOptions);
                    if (evt is not null && evt.Timestamp < cutoff)
                    {
                        archive.Add(line);
                    }
                    else
                    {
                        keep.Add(line);
                    }
                }
                catch
                {
                    keep.Add(line);
                }
            }

            var bakPath = _journalPath + ".bak";
            RotateArchiveIfNeeded(bakPath);

            if (archive.Count > 0)
            {
                File.AppendAllLines(bakPath, archive);
            }

            File.WriteAllLines(_journalPath, keep);
        }
        catch
        {
            // 异常不抛出
        }
        finally
        {
            Interlocked.Exchange(ref _rotating, 0);
        }
    }

    private const long MaxArchiveSizeBytes = 100 * 1024 * 1024;
    private const int ArchiveRetentionDays = 30;

    private void RotateArchiveIfNeeded(string bakPath)
    {
        try
        {
            if (!File.Exists(bakPath) || new FileInfo(bakPath).Length <= MaxArchiveSizeBytes)
            {
                return;
            }

            var cutoff = DateTimeOffset.UtcNow.AddDays(-ArchiveRetentionDays);
            var keepLines = new List<string>();
            foreach (var line in File.ReadLines(bakPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<DiagnosticEvent>(line, JsonOptions);
                    if (evt is not null && evt.Timestamp >= cutoff)
                        keepLines.Add(line);
                }
                catch { keepLines.Add(line); }
            }
            File.WriteAllLines(bakPath, keepLines);
        }
        catch
        {
            // archive rotation failure is non-critical
        }
    }

    private List<DiagnosticEvent> ReadFromFile(DateTimeOffset dateFrom, DateTimeOffset cacheOldest, int limit)
    {
        if (!File.Exists(_journalPath))
        {
            return [];
        }

        try
        {
            // Stream through file, keep only last 5000 lines in memory via ring buffer
            var buffer = new LinkedList<string>();
            foreach (var line in File.ReadLines(_journalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                buffer.AddLast(line);
                if (buffer.Count > 5000) buffer.RemoveFirst();
            }

            var results = new List<DiagnosticEvent>();
            for (var node = buffer.Last; node != null && results.Count < limit; node = node.Previous)
            {
                try
                {
                    var evt = JsonSerializer.Deserialize<DiagnosticEvent>(node.Value, JsonOptions);
                    if (evt is not null && evt.Timestamp >= dateFrom && evt.Timestamp < cacheOldest)
                    {
                        results.Add(evt);
                    }
                }
                catch
                {
                    // skip corrupted lines
                }
            }

            results.Reverse();
            return results;
        }
        catch
        {
            return [];
        }
    }

    private static bool IsHighPriority(string? level)
    {
        return string.Equals(level, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(level, "critical", StringComparison.OrdinalIgnoreCase)
            || string.Equals(level, "warning", StringComparison.OrdinalIgnoreCase);
    }

    private void LoadFromFile()
    {
        if (!File.Exists(_journalPath))
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                foreach (var line in File.ReadLines(_journalPath)
                    .Where(static l => !string.IsNullOrWhiteSpace(l))
                    .TakeLast(ReloadLimit))
                {
                    try
                    {
                        var evt = JsonSerializer.Deserialize<DiagnosticEvent>(line, JsonOptions);
                        if (evt is not null)
                        {
                            if (IsHighPriority(evt.Level))
                            {
                                _highPriorityCache.Add(evt);
                            }
                            else
                            {
                                _lowPriorityCache.Add(evt);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[FileDiagnosticsService] Skipping corrupted journal line: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FileDiagnosticsService] Failed to load journal file: {ex.Message}");
            }
        }
    }

    private void AppendToFile(DiagnosticEvent diagnosticEvent)
    {
        try
        {
            var line = JsonSerializer.Serialize(diagnosticEvent, JsonOptions);
            File.AppendAllText(_journalPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FileDiagnosticsService] Failed to write journal file: {ex.Message}");
        }
    }

    private async Task RewriteFileAsync(DateTimeOffset? keepFrom, CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
        {
            return;
        }

        if (!keepFrom.HasValue)
        {
            try
            {
                File.Delete(_journalPath);
            }
            catch
            {
                // ignore
            }

            return;
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(_journalPath, cancellationToken);
            var kept = lines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Where(l =>
                {
                    try
                    {
                        var evt = JsonSerializer.Deserialize<DiagnosticEvent>(l, JsonOptions);
                        return evt is not null && evt.Timestamp >= keepFrom.Value;
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToArray();

            await File.WriteAllLinesAsync(_journalPath, kept, cancellationToken);
        }
        catch
        {
            // 重写失败不抛出，文件保持原样
        }
    }
}
