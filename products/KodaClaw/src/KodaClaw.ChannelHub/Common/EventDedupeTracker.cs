using System.Collections.Concurrent;

namespace KodaClaw.ChannelHub.Common;

internal sealed class EventDedupeTracker
{
    public const int DefaultWindowSize = 500;

    private readonly int _windowSize;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentDictionary<string, byte> _set = new(StringComparer.Ordinal);

    public EventDedupeTracker(int windowSize = DefaultWindowSize)
    {
        if (windowSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "Window size must be positive.");
        }

        _windowSize = windowSize;
    }

    public bool TryTrack(string eventId)
    {
        if (!_set.TryAdd(eventId, 0))
        {
            return false;
        }

        _queue.Enqueue(eventId);
        while (_queue.Count > _windowSize)
        {
            if (_queue.TryDequeue(out var oldest))
            {
                _set.TryRemove(oldest, out _);
            }
        }

        return true;
    }
}
