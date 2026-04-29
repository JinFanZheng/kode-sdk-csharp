namespace KodaClaw.Contracts.Diagnostics;

public sealed record PromptReportDelta(
    DateTimeOffset? PreviousGeneratedAt,
    int CharacterCountDelta,
    bool TruncationStateChanged,
    IReadOnlyList<string> AddedContextFiles,
    IReadOnlyList<string> RemovedContextFiles);
