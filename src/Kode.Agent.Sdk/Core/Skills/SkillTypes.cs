using System.Text.Json;

namespace Kode.Agent.Sdk.Core.Skills;

/// <summary>
/// SKILL.md frontmatter metadata.
/// </summary>
public record SkillMetadata
{
    /// <summary>
    /// Skill name, required, 1-64 characters, kebab-case format.
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Skill description, required, 1-1024 characters.
    /// </summary>
    public required string Description { get; init; }
    
    /// <summary>
    /// License, e.g., "Apache-2.0".
    /// </summary>
    public string? License { get; init; }
    
    /// <summary>
    /// Compatibility notes, e.g., "claude-3.5-sonnet, gpt-4".
    /// </summary>
    public string? Compatibility { get; init; }
    
    /// <summary>
    /// Allowed tools for this skill.
    /// </summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }
    
    /// <summary>
    /// Custom metadata.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>
/// Skill resources directory.
/// </summary>
public record SkillResources
{
    /// <summary>
    /// Executable script files.
    /// </summary>
    public IReadOnlyList<string>? Scripts { get; init; }
    
    /// <summary>
    /// Reference documents.
    /// </summary>
    public IReadOnlyList<string>? References { get; init; }
    
    /// <summary>
    /// Asset files (templates, icons, etc.).
    /// </summary>
    public IReadOnlyList<string>? Assets { get; init; }
}

/// <summary>
/// Complete skill definition.
/// </summary>
public record Skill : SkillMetadata
{
    /// <summary>
    /// Skill directory absolute path.
    /// </summary>
    public required string Path { get; init; }
    
    /// <summary>
    /// SKILL.md Markdown content (loaded after activation).
    /// </summary>
    public string? Body { get; set; }
    
    /// <summary>
    /// Resources directory content.
    /// </summary>
    public SkillResources? Resources { get; init; }
    
    /// <summary>
    /// Parse timestamp.
    /// </summary>
    public long? LoadedAt { get; set; }
    
    /// <summary>
    /// Activation timestamp.
    /// </summary>
    public long? ActivatedAt { get; set; }
}

/// <summary>
/// Skills configuration.
/// </summary>
public record SkillsConfig
{
    /// <summary>
    /// Skills search paths.
    /// </summary>
    public required IReadOnlyList<string> Paths { get; init; }
    
    /// <summary>
    /// Whitelist: only load these skills.
    /// </summary>
    public IReadOnlyList<string>? Include { get; init; }
    
    /// <summary>
    /// Blacklist: exclude these skills.
    /// </summary>
    public IReadOnlyList<string>? Exclude { get; init; }
    
    /// <summary>
    /// Trusted sources: allow script execution for these skills.
    /// </summary>
    public IReadOnlyList<string>? Trusted { get; init; }
    
    /// <summary>
    /// Whether to validate format on load.
    /// </summary>
    public bool ValidateOnLoad { get; init; } = true;

    /// <summary>
    /// Skills to activate automatically at session start.
    /// Missing skill names are silently skipped.
    /// </summary>
    public IReadOnlyList<string>? AutoActivate { get; init; }

    /// <summary>
    /// Controls how discovered skill metadata is injected into the system prompt.
    /// Defaults to <see cref="SkillsInjectionMode.Full"/>, aligned with the Agent Skills
    /// specification's progressive-disclosure tier 1 (name + description loaded at startup).
    /// Use <see cref="SkillsInjectionMode.NamesOnly"/> or <see cref="SkillsInjectionMode.None"/>
    /// to reduce system-prompt pressure when the skill library is very large; the agent
    /// can still enumerate details on demand via the <c>skill_list</c> tool.
    /// </summary>
    public SkillsInjectionMode InjectionMode { get; init; } = SkillsInjectionMode.Full;
}

/// <summary>
/// Controls the amount of skill metadata copied into the agent's system prompt at startup.
/// </summary>
public enum SkillsInjectionMode
{
    /// <summary>
    /// Inject each discovered skill's name, description, and location (spec-aligned default).
    /// </summary>
    Full,

    /// <summary>
    /// Inject only the skill names. The agent is directed to call <c>skill_list</c> for details.
    /// </summary>
    NamesOnly,

    /// <summary>
    /// Skip injection entirely. The agent must call <c>skill_list</c> to discover skills.
    /// </summary>
    None
}

/// <summary>
/// Skill activation record.
/// </summary>
public record SkillActivation
{
    /// <summary>
    /// Skill name.
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Activation timestamp.
    /// </summary>
    public required long ActivatedAt { get; init; }
    
    /// <summary>
    /// Activation source.
    /// </summary>
    public required SkillActivationSource ActivatedBy { get; init; }
    
    /// <summary>
    /// Granted tools.
    /// </summary>
    public IReadOnlyList<string>? ToolsGranted { get; init; }
}

/// <summary>
/// Skill activation source.
/// </summary>
public enum SkillActivationSource
{
    Auto,
    Agent,
    User
}

/// <summary>
/// Skills state for persistence.
/// </summary>
public record SkillsState
{
    public required IReadOnlyList<string> Discovered { get; init; }
    public required IReadOnlyList<SkillActivation> Activated { get; init; }
    public required long LastDiscoveryAt { get; init; }
}
