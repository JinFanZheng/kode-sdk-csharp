namespace KodaClaw.Contracts.System;

public sealed record StorageUsageResponse
{
    public required SessionStorageUsage Main { get; init; }
    public required SessionStorageUsage Auto { get; init; }
    public required SessionStorageUsage Channel { get; init; }
    public long TotalSizeBytes { get; init; }
}

public sealed record SessionStorageUsage
{
    public int Count { get; init; }
    public long SizeBytes { get; init; }
}
