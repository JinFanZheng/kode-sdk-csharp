namespace KodaClaw.Contracts.Approvals;

public sealed record ApprovalQueryResponse(IReadOnlyList<Approval> Items);
