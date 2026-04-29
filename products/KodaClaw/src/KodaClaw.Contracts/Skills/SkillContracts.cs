namespace KodaClaw.Contracts.Skills;

public sealed record SkillDescriptor(
    string Name,
    string? Description,
    string Source,
    string Path,
    bool HasResources,
    string Kind,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> AllowedTools,
    string? Version,
    string? Compatibility);
