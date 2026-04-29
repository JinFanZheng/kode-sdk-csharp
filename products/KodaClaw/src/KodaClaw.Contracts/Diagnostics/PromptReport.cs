namespace KodaClaw.Contracts.Diagnostics;

public sealed record PromptReport(
    string ProfileId,
    string SystemPrompt,
    int CharacterCount,
    IReadOnlyList<string> LoadedContextFiles,
    DateTimeOffset GeneratedAt,
    int? CharacterBudget = null,
    int? RemainingCharacterBudget = null,
    bool WasTruncated = false,
    IReadOnlyList<string>? TruncatedContextFiles = null,
    IReadOnlyList<string>? TruncationNotes = null);
