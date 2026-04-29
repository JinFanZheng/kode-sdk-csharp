namespace KodaClaw.Contracts.Canvas;

public sealed record CanvasEntryResponse(
    string EntryUrl,
    string EntryPath,
    string? ArtifactId = null,
    string? Route = null,
    string? Title = null);
