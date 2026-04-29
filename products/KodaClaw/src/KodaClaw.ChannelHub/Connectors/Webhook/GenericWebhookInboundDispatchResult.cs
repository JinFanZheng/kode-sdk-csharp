using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;

namespace KodaClaw.ChannelHub.Connectors.Webhook;

public sealed record GenericWebhookInboundDispatchResult(
    bool Accepted,
    ChannelEventEnvelope? Event = null,
    string? RejectionCode = null,
    string? RejectionMessage = null);
