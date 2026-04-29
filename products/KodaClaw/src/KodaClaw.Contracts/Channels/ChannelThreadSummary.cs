using KodaClaw.Contracts.Sessions;

namespace KodaClaw.Contracts.Channels;

public sealed record ChannelThreadSummary(
    string BindingId,
    ChannelConnectorKind ConnectorKind,
    string AccountId,
    string ExternalThreadId,
    ChannelThreadType ThreadType,
    string SessionId,
    SessionKind SessionKind,
    string DisplayTitle,
    DeliveryMode DeliveryMode,
    ChannelAccountState AccountState,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastInboundAt = null,
    DateTimeOffset? LastOutboundAt = null,
    string? LastMessagePreview = null,
    string? PendingApprovalId = null,
    bool HasPendingDraft = false,
    ChannelTurnOutcome? LastTurnOutcome = null);
