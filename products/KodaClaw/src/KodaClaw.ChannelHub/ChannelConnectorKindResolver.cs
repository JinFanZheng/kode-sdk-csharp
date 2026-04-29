using System.Collections.Concurrent;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;

namespace KodaClaw.ChannelHub;

public sealed class ChannelConnectorKindResolver
{
    private readonly ConcurrentDictionary<ChannelConnectorKind, IChannelConnector> _connectors = new();

    public ChannelConnectorKindResolver(IEnumerable<IChannelConnector> connectors)
    {
        foreach (var connector in connectors)
        {
            _connectors[connector.Kind] = connector;
        }
    }

    public bool TryGet(ChannelConnectorKind kind, out IChannelConnector? connector)
        => _connectors.TryGetValue(kind, out connector);

    public IReadOnlyCollection<IChannelConnector> GetAll() => (IReadOnlyCollection<IChannelConnector>)_connectors.Values;
}
