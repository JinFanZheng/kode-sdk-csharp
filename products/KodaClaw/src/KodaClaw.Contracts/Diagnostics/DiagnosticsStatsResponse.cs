namespace KodaClaw.Contracts.Diagnostics;

public sealed record DiagnosticsStatsResponse(
    int TotalEvents,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<DiagnosticsSourceStats> BySource,
    DateTimeOffset? OldestEvent,
    DateTimeOffset? NewestEvent);

public sealed record DiagnosticsSourceStats(
    string Source,
    int Count,
    int ErrorCount,
    int WarningCount);
