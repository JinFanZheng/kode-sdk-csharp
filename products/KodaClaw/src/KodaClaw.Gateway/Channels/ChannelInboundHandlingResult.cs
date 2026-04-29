using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Inbound;
using KodaClaw.ChannelHub.Turn;

namespace KodaClaw.Gateway.Channels;

internal sealed record ChannelInboundHandlingResult(
    ChannelInboundProcessingResult Processing,
    ChannelTurnOrchestrationResult? Turn = null,
    bool RuntimeExecuted = false,
    string? SkipReason = null);
