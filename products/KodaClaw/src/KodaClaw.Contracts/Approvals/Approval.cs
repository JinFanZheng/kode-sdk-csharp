namespace KodaClaw.Contracts.Approvals;

public sealed record Approval(
    string Id,
    ApprovalKind Kind,
    ApprovalStatus Status,
    string Title,
    string Summary,
    string Source,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    string? SessionId = null,
    string? CorrelationId = null,
    string? InboxItemId = null,
    string? PayloadJson = null,
    DateTimeOffset? DecidedAt = null,
    string? DecidedBy = null,
    string? DecisionNote = null);
