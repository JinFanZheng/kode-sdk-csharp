namespace KodaClaw.Contracts.Sessions;

public sealed record SessionMessageItem(
    string Id,
    string Role,
    string Text,
    long? Timestamp,
    string? Thinking = null,
    string? ToolName = null,
    string? InputPreview = null);

public sealed record SessionMessagesResponse(
    IReadOnlyList<SessionMessageItem> Items,
    int TotalCount,
    bool HasMore);
