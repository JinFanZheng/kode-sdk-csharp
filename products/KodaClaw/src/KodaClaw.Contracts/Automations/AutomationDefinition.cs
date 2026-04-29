namespace KodaClaw.Contracts.Automations;

public sealed record AutomationDefinition(
    string Id,
    string Title,
    string Prompt,
    AutomationDefinitionSource Source,
    string? SourcePath,
    string CronExpression,
    bool Enabled,
    IReadOnlyList<string>? InputPaths,
    string? ModelId,
    IReadOnlyList<string>? NotificationChannels,
    AutomationNotifyMode NotifyMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt,
    AutomationRunStatus? LastRunStatus,
    string? LastError);
