using KodaClaw.Contracts.Channels;

namespace KodaClaw.Contracts.Automations;

public sealed record AutomationChannelMessageLink(
    string Id,
    string AutomationId,
    string RunId,
    string SessionId,
    string BindingId,
    ChannelConnectorKind ConnectorKind,
    string AccountId,
    string ExternalThreadId,
    string ExternalMessageId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null,
    string? Summary = null);

