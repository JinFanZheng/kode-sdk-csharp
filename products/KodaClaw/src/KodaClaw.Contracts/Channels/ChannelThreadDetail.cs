using KodaClaw.Contracts.Sessions;

namespace KodaClaw.Contracts.Channels;

public sealed record ChannelThreadDetail(
    ChannelAccount Account,
    ThreadBinding Binding,
    ChannelPolicy Policy,
    DeliveryRule DeliveryRule,
    IReadOnlyList<ChannelAuditEntry> RecentAudit,
    SessionSummary? Session = null,
    string? PendingApprovalId = null,
    bool HasPendingDraft = false,
    IReadOnlyList<string>? PolicyEvidence = null,
    ChannelTurnOutcome? LastTurnOutcome = null);
