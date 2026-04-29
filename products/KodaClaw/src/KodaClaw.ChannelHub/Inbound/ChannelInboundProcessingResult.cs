using KodaClaw.Contracts.Channels;

namespace KodaClaw.ChannelHub.Inbound;

public sealed record ChannelInboundProcessingResult(
    ThreadBinding Binding,
    ChannelPolicy Policy,
    DeliveryRule DeliveryRule,
    bool CreatedBinding,
    ChannelAuditEntry? AuditEntry = null);
