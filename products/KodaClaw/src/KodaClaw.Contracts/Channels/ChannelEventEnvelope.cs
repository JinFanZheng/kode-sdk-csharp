using KodaClaw.Contracts.Media;

namespace KodaClaw.Contracts.Channels;

public sealed record ChannelEventEnvelope(
    string EventId,
    ChannelEventType EventType,
    ChannelConnectorKind ConnectorKind,
    string AccountId,
    string ExternalThreadId,
    ChannelThreadType ThreadType,
    DateTimeOffset OccurredAt,
    ChannelIdentity? Sender = null,
    ChannelIdentity? Recipient = null,
    string? ExternalMessageId = null,
    string? Text = null,
    string? CorrelationId = null,
    string? BindingId = null,
    string? SessionId = null,
    string? MetadataJson = null,
    DeliveryMode? DefaultDeliveryMode = null,
    IReadOnlyList<MediaReference>? MediaAttachments = null);
