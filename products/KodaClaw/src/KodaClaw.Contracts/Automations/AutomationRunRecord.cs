namespace KodaClaw.Contracts.Automations;

public sealed record AutomationRunRecord(
    string RunId,
    string AutomationId,
    AutomationRunStatus Status,
    string Trigger,
    int Attempt,
    string? SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Summary,
    string? ErrorMessage);
