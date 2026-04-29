namespace KodaClaw.Contracts.Media;

/// <summary>
/// Metadata for a stored media file.
/// </summary>
public record MediaMeta(
    string Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset StoredAt,
    int? DurationMs = null,
    bool IsPinned = false,
    string? Source = null,
    string? ExternalMessageId = null);
