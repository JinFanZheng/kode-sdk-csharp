namespace KodaClaw.Contracts;

public sealed record ThreadBinding(
    string Id,
    ChannelConnectorKind ConnectorKind,
    string AccountId,
    string ExternalThreadId,
    ChannelThreadType ThreadType,
    string SessionId,
    SessionKind SessionKind,
    ChannelIdentity ChannelIdentity,
    string PolicyId,
    string DeliveryRuleId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastInboundAt = null,
    DateTimeOffset? LastOutboundAt = null,
    string? LastMessagePreview = null,
    DeliveryMode? DeliveryModeOverride = null,
    string? PendingModelOverride = null,
    string? ActiveModelId = null,
    bool ThinkingEnabled = false,
    bool? StreamOverride = null);
