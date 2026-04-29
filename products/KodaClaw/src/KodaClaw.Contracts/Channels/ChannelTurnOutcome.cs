namespace KodaClaw.Contracts.Channels;

public sealed record ChannelTurnOutcome(
    ChannelTurnOutcomeKind Kind,
    string Summary,
    DateTimeOffset OccurredAt,
    string? ReplyText = null,
    DeliveryMode? DeliveryMode = null,
    string? ApprovalId = null,
    string? InboxItemId = null,
    string? DraftId = null,
    string? SourceEventId = null,
    string? ReasonCode = null,
    bool? HasExplicitMention = null);
