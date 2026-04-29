namespace KodaClaw.Contracts.Sessions;

public sealed record SessionStatusSummary(
    bool IsActiveMainSession,
    string? BreakpointState,
    int MessageCount,
    int PendingApprovalCount);
