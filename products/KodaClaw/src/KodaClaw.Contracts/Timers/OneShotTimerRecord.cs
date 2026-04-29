namespace KodaClaw.Contracts.Timers;

public sealed record OneShotTimerRecord(
    string Id,
    string? Title,
    string Prompt,
    DateTimeOffset FireAt,
    OneShotTimerStatus Status,
    IReadOnlyList<string>? Channels,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FiredAt,
    string? ErrorMessage
);
