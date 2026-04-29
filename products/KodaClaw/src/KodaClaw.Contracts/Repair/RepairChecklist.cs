namespace KodaClaw.Contracts.Repair;

public sealed record RepairChecklist(
    DateTimeOffset GeneratedAt,
    string Scope,
    RepairChecklistSummary Summary,
    IReadOnlyList<RepairChecklistItem> Items);
