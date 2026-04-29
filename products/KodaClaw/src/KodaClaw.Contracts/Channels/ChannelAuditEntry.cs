namespace KodaClaw.Contracts.Channels;

public sealed record ChannelAuditEntry(
    string Id,
    string BindingId,
    ChannelConnectorKind ConnectorKind,
    string AccountId,
    string ExternalThreadId,
    ChannelThreadType ThreadType,
    string EventType,
    DateTimeOffset CreatedAt,
    string? SessionId = null,
    string? ApprovalId = null,
    DeliveryMode? DeliveryMode = null,
    string? ExternalMessageId = null,
    string? Summary = null,
    string? MetadataJson = null);
