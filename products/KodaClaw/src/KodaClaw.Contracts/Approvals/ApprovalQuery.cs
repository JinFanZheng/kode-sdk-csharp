namespace KodaClaw.Contracts.Approvals;

public sealed record ApprovalQuery(
    ApprovalStatus? Status = null,
    ApprovalKind? Kind = null,
    string? SessionId = null,
    int Limit = 50);
