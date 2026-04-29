namespace KodaClaw.Contracts.Workspace;

public sealed record PersonaPreset(
    string PresetId,
    string DisplayName,
    string TagLine,
    string Description,
    string[] Tags,
    string SoulMarkdown,
    string IdentityMarkdown
);
