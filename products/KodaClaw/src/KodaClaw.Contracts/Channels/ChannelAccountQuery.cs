namespace KodaClaw.Contracts.Channels;

public sealed record ChannelAccountQuery(
    ChannelConnectorKind? ConnectorKind = null,
    ChannelAccountState? State = null,
    int Limit = 50,
    int Offset = 0);
