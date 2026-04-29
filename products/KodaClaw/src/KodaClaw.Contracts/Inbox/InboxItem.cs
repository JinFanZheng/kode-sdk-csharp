namespace KodaClaw.Contracts.Inbox;

public sealed record InboxItem(
    string Id,
    InboxItemKind Kind,
    InboxItemStatus Status,
    string Title,
    string Summary,
    string Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool RequiresAction = false,
    string? Route = null,
    string? SessionId = null,
    string? CorrelationId = null,
    string? ApprovalId = null,
    string? PayloadJson = null,
    DateTimeOffset? ResolvedAt = null);
