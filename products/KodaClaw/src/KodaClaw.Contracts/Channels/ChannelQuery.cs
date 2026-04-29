using KodaClaw.Contracts.Sessions;

namespace KodaClaw.Contracts.Channels;

public sealed record ChannelQuery(
    ChannelConnectorKind? ConnectorKind = null,
    string? AccountId = null,
    ChannelThreadType? ThreadType = null,
    SessionKind? SessionKind = null,
    string? SessionId = null,
    int Limit = 50);
