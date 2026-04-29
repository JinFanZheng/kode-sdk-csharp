namespace KodaClaw.Contracts.System;

public sealed record UpdateStateResponse(
    DateTimeOffset GeneratedAt,
    string ArtifactPath,
    string ManifestSource,
    IReadOnlyList<UpdateComponentState> Components,
    IReadOnlyList<string> OperatorNotes);
