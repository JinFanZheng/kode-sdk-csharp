namespace Kode.Agent.Sdk.Core.Agent;

public enum PendingKind
{
    User,
    Reminder
}

public record ReminderOptions
{
    public bool SkipStandardEnding { get; init; }
    public string? Category { get; init; }
    public string? Priority { get; init; }
    public bool? Persistent { get; init; }
    public string? Label { get; init; }
}

public record SendOptions
{
    public PendingKind Kind { get; init; } = PendingKind.User;
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
    public ReminderOptions? Reminder { get; init; }

    /// <summary>
    /// Optional key used by <see cref="MessageQueue.Send(string, SendOptions?)"/> to drop
    /// duplicates that are still pending. When a new send arrives with a key equal to any
    /// still-queued entry, the new send is ignored and the existing id is returned.
    /// Intended for reminder bursts (e.g. file-change events firing repeatedly) where
    /// collapsing to a single notification preserves correctness without flooding context.
    /// </summary>
    public string? DedupKey { get; init; }
}

public record PendingMessage
{
    public required string Id { get; init; }
    public required Message Message { get; init; }
    public required PendingKind Kind { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
    public string? DedupKey { get; init; }
}

public record MessageQueueOptions
{
    public required Func<string, ReminderOptions?, string> WrapReminder { get; init; }
    public required Func<Message, PendingKind, CancellationToken, Task> AddMessageAsync { get; init; }
    public required Func<CancellationToken, Task> PersistAsync { get; init; }
    public required Action EnsureProcessing { get; init; }
}

/// <summary>
/// TS-aligned message queue: buffers user/reminder messages and flushes them atomically before processing.
/// </summary>
public sealed class MessageQueue
{
    private readonly MessageQueueOptions _options;
    private readonly List<PendingMessage> _pending = [];
    private readonly System.Threading.Lock _lock = new();
    private bool _completed;

    public MessageQueue(MessageQueueOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Sends a multi-modal user message (text + images).
    /// </summary>
    public string Send(IReadOnlyList<ContentBlock> parts, SendOptions? opts = null)
    {
        opts ??= new SendOptions();
        var id = $"msg-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";

        var pending = new PendingMessage
        {
            Id = id,
            Message = new Message
            {
                Role = MessageRole.User,
                Content = parts
            },
            Kind = opts.Kind,
            Metadata = opts.Metadata is null
                ? new Dictionary<string, object?> { ["id"] = id }
                : new Dictionary<string, object?>(opts.Metadata) { ["id"] = id }
        };

        lock (_lock)
        {
            if (_completed)
            {
                throw new InvalidOperationException("MessageQueue is completed");
            }
            _pending.Add(pending);
        }

        if (opts.Kind == PendingKind.User)
        {
            _options.EnsureProcessing();
        }

        return id;
    }

    public string Send(string text, SendOptions? opts = null)
    {
        opts ??= new SendOptions();
        var kind = opts.Kind;
        var payload = kind == PendingKind.Reminder ? _options.WrapReminder(text, opts.Reminder) : text;
        var id = $"msg-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";

        var pending = new PendingMessage
        {
            Id = id,
            Message = Message.User(payload),
            Kind = kind,
            Metadata = opts.Metadata is null
                ? new Dictionary<string, object?> { ["id"] = id }
                : new Dictionary<string, object?>(opts.Metadata) { ["id"] = id },
            DedupKey = opts.DedupKey
        };

        lock (_lock)
        {
            if (_completed)
            {
                throw new InvalidOperationException("MessageQueue is completed");
            }
            if (opts.DedupKey != null)
            {
                foreach (var existing in _pending)
                {
                    if (existing.DedupKey == opts.DedupKey)
                    {
                        return existing.Id;
                    }
                }
            }
            _pending.Add(pending);
        }

        if (kind == PendingKind.User)
        {
            _options.EnsureProcessing();
        }

        return id;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        PendingMessage[] batch;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
        }

        // Failure propagates: pending entries remain in the queue for a subsequent retry.
        foreach (var entry in batch)
        {
            await _options.AddMessageAsync(entry.Message, entry.Kind, cancellationToken);
        }

        // Persist success before removing from queue
        await _options.PersistAsync(cancellationToken);

        lock (_lock)
        {
            // Remove only entries that were flushed (leave any newly queued ones intact)
            var ids = new HashSet<string>(batch.Select(b => b.Id), StringComparer.Ordinal);
            _pending.RemoveAll(item => ids.Contains(item.Id));
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            _pending.Clear();
        }
    }
}
