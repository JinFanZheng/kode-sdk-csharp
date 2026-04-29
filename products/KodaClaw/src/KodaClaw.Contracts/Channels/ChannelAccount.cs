namespace KodaClaw.Contracts.Channels;

public sealed record ChannelAccount(
    string Id,
    ChannelConnectorKind ConnectorKind,
    string DisplayName,
    ChannelAccountState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ExternalAccountId = null,
    string? CredentialReference = null,
    string? Description = null,
    string? ConfigurationJson = null,
    bool InboundEnabled = true,
    DateTimeOffset? LastConnectedAt = null,
    DateTimeOffset? LastDisconnectedAt = null,
    string? LastError = null);
