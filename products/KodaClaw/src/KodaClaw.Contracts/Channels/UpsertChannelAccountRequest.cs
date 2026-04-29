namespace KodaClaw.Contracts.Channels;

public sealed record UpsertChannelAccountRequest(
    string Id,
    ChannelConnectorKind ConnectorKind,
    string DisplayName,
    string? ExternalAccountId = null,
    string? CredentialReference = null,
    string? Description = null,
    string? ConfigurationJson = null,
    bool InboundEnabled = true);
