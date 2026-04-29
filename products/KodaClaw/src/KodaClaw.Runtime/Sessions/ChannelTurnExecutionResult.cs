using Kode.Agent.Sdk.Core.Abstractions;

namespace KodaClaw.Runtime.Sessions;

public sealed record ChannelTurnExecutionResult(
    ChannelSessionHandle Session,
    AgentRunResult RunResult,
    string RawResponse,
    ChannelReplyProposal? Proposal,
    bool HasExplicitMention);
