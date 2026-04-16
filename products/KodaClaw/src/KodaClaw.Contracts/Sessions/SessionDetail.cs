namespace KodaClaw.Contracts;

public sealed record SessionDetail(
    string SessionId,
    SessionKind SessionKind,
    SessionStatusSummary Status,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastEventAt,
    int UserMessageCount,
    int AssistantMessageCount,
    int ToolCallCount,
    int LastSfpIndex,
    IReadOnlyList<string> PendingApprovalCallIds,
    PromptReport? PromptReport = null,
    PromptReportDelta? PromptReportDelta = null,
    IReadOnlyList<PromptReport>? RecentPromptReports = null,
    string? Title = null,
    string? AccountModelId = null,
    string? AccountModelName = null,
    int ModelCapabilities = 0);
