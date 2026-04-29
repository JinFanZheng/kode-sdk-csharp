namespace KodaClaw.Contracts.Canvas;

public sealed record UpsertCanvasArtifactRequest(
    string Id,
    string Title,
    CanvasArtifactKind Kind,
    string Summary,
    string Source,
    string EntryPath,
    string AssetDirectory,
    string? Route = null,
    string? SessionId = null,
    string? CorrelationId = null,
    string? MetadataJson = null);
