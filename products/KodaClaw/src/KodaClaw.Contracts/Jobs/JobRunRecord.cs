namespace KodaClaw.Contracts.Jobs;

/// <summary>
/// Phase 1a 不使用，仅预定义给 Phase 1b 使用。
/// </summary>
public sealed record JobRunRecord(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    int RetryCount,
    string? Result,
    DateTimeOffset? NextRunSet);
