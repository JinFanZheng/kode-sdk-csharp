namespace KodaClaw.Contracts.Repair;

public sealed record RepairChecklistSummary(
    int TotalCount,
    int BlockingCount,
    int ActionRequiredCount,
    int WarningCount,
    int InfoCount);
