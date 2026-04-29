namespace KodaClaw.Contracts.Memory;

/// <summary>
/// Lightweight DTO representing a memory entry parsed from file metadata.
/// Replaces the SQLite-backed <c>MemoryEntry</c>.
/// </summary>
public sealed record MemoryFileEntry(
    string Key,
    string Title,
    string Priority,
    string Status,
    string? Created,
    string SourcePath,
    string[]? Tags);
