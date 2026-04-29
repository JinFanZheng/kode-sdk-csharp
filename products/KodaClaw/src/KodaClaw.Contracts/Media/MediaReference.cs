namespace KodaClaw.Contracts.Media;

/// <summary>
/// A reference to a stored media file, used as an outbound attachment.
/// </summary>
public record MediaReference(
    string MediaId,
    string ContentType,
    string? FileName = null,
    long? SizeBytes = null,
    int? DurationMs = null);
