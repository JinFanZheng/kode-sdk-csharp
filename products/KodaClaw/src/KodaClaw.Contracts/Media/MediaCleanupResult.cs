namespace KodaClaw.Contracts.Media;

public record MediaCleanupResult(int DeletedCount, long FreedBytes, int PinnedCount);
