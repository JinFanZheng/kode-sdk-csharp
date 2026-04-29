using KodaClaw.ChannelHub.Inbound;
using KodaClaw.Contracts.Channels;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;

namespace KodaClaw.ChannelHub.Turn;

public sealed record ChannelTurnOrchestrationResult(
    ChannelInboundProcessingResult Processing,
    ChannelTurnOutcome Outcome,
    bool ExecutedTurn,
    ChannelTurnExecutionResult? Execution = null);
