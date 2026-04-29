namespace KodaClaw.Contracts.Automations;

public sealed record AutomationRunsQueryResponse(
    IReadOnlyList<AutomationRunRecord> Items);
