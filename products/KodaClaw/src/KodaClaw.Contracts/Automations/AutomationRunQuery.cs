namespace KodaClaw.Contracts.Automations;

public sealed record AutomationRunQuery(
    string? AutomationId = null,
    AutomationRunStatus? Status = null,
    int Limit = 50);
