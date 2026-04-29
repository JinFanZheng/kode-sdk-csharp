using KodaClaw.Contracts.Media;

namespace KodaClaw.Contracts.Channels;

public sealed record ChannelOutboundDraft(
    string DraftId,
    string BindingId,
    ChannelConnectorKind ConnectorKind,
    string AccountId,
    string ExternalThreadId,
    string MessageText,
    ChannelThreadType ThreadType = ChannelThreadType.DirectMessage,
    DeliveryMode DeliveryMode = DeliveryMode.AutoSend,
    DateTimeOffset CreatedAt = default,
    string? SessionId = null,
    string? CorrelationId = null,
    string? ApprovalId = null,
    string? MetadataJson = null,
    IReadOnlyList<MediaReference>? MediaAttachments = null,
    OutboundMessageFormat Format = OutboundMessageFormat.Auto);
