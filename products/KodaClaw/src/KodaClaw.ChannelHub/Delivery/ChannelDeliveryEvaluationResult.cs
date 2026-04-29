using KodaClaw.Contracts.Channels;

namespace KodaClaw.ChannelHub.Delivery;

public sealed record ChannelDeliveryEvaluationResult(
    ChannelDeliveryDisposition Disposition,
    DeliveryMode DeliveryMode,
    string? ApprovalId = null,
    string? InboxItemId = null,
    string? ApprovalToken = null);
