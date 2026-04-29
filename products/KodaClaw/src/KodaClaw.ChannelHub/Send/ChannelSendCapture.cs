using System.Collections.Concurrent;

namespace KodaClaw.ChannelHub.Send;

public sealed class ChannelSendCapture : IChannelSendCapture
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _captures =
        new(StringComparer.Ordinal);

    public void Record(string bindingId, string text)
    {
        _captures.GetOrAdd(bindingId, _ => new ConcurrentQueue<string>()).Enqueue(text);
    }

    public IReadOnlyList<string> GetAndClear(string bindingId)
    {
        return _captures.TryRemove(bindingId, out var queue) ? [.. queue] : [];
    }
}
