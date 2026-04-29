namespace KodaClaw.Contracts.Memory;

/// <summary>
/// File-based memory service that scans workspace directories
/// instead of querying SQLite.
/// </summary>
public interface IMemoryFileService
{
    Task<MemoryFileStats> GetStatsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryFileEntry>> ListEntriesAsync(
        string? statusFilter = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<bool> PromoteEntryAsync(string key, CancellationToken cancellationToken = default);
}

public sealed record MemoryFileStats(
    int ActiveCount,
    int DormantCount,
    int ArchivedCount,
    int TopicsCount,
    int SessionsCount);
