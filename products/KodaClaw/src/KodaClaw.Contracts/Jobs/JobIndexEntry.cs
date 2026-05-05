namespace KodaClaw.Contracts.Jobs;

public sealed record JobIndexEntry(
    string Name,
    string Type,
    string Status,
    string? NextRunAt);
