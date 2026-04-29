namespace KodaClaw.Contracts.Canvas;

public sealed record CanvasArtifact(
    string Id,
    string Title,
    CanvasArtifactKind Kind,
    string Summary,
    string Source,
    string EntryPath,
    string AssetDirectory,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Route = null,
    string? SessionId = null,
    string? CorrelationId = null,
    string? MetadataJson = null);
