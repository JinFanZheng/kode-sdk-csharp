using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kode.Agent.Sdk.Core.Templates;

namespace Kode.Agent.Tools.Orchestration.Internal;

/// <summary>
/// Loaded template with execution metadata not stored in AgentTemplateDefinition.
/// </summary>
internal record LoadedTemplate(
    AgentTemplateDefinition Definition,
    int MaxIterations = 20,
    int MaxContextTokens = 80_000,
    IReadOnlyList<string>? AutoActivateSkills = null
);

/// <summary>
/// Loads agent template definitions from JSON files.
/// </summary>
internal static class TemplateFileLoader
{
    // id must match [a-z0-9_-]+
    private static readonly Regex _idRegex = new(@"^[a-z0-9_-]+$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Loads and parses a template from a JSON file.
    /// Relative paths are resolved against <paramref name="baseDir"/> (or current directory if null).
    /// </summary>
    public static LoadedTemplate LoadFromFile(string filePath, string? baseDir = null)
    {
        var resolved = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetFullPath(filePath, baseDir ?? Directory.GetCurrentDirectory());

        if (!File.Exists(resolved))
            throw new FileNotFoundException($"Template file not found: '{resolved}'", resolved);

        string json;
        try
        {
            json = File.ReadAllText(resolved);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to read template file '{resolved}': {ex.Message}", ex);
        }

        return ParseJson(json, resolved);
    }

    /// <summary>
    /// Parses a template from a JSON string. <paramref name="sourcePath"/> is used in error messages only.
    /// </summary>
    internal static LoadedTemplate ParseJson(string json, string sourcePath = "<string>")
    {
        AgentTemplateDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<AgentTemplateDto>(json, _jsonOptions)
                  ?? throw new InvalidOperationException($"Template JSON deserialized to null: '{sourcePath}'");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in template '{sourcePath}': {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(dto.Id))
            throw new InvalidOperationException($"Template '{sourcePath}' is missing required field 'id'.");

        // Problem 4: validate id format [a-z0-9_-]
        if (!_idRegex.IsMatch(dto.Id))
            throw new InvalidOperationException(
                $"Template '{sourcePath}' has invalid 'id' value '{dto.Id}'. " +
                "Only lowercase letters, digits, underscores, and hyphens are allowed.");

        if (string.IsNullOrWhiteSpace(dto.SystemPrompt))
            throw new InvalidOperationException($"Template '{sourcePath}' is missing required field 'system_prompt'.");

        var toolsConfig = dto.Tools == null
            ? ToolsConfig.All()
            : dto.Tools.AllowAll == true
                ? ToolsConfig.All()
                : dto.Tools.AllowedTools is { Count: > 0 }
                    ? ToolsConfig.Specific(dto.Tools.AllowedTools)
                    : ToolsConfig.All();

        PermissionConfig? permission = null;
        if (dto.Permission != null)
        {
            permission = new PermissionConfig
            {
                Mode = dto.Permission.Mode ?? "auto",
                RequireApprovalTools = dto.Permission.RequireApproval,
                DenyTools = dto.Permission.Deny,
            };
        }

        SubAgentConfig? subAgents = null;
        if (dto.Runtime?.SubAgents != null)
        {
            subAgents = new SubAgentConfig
            {
                Depth = dto.Runtime.SubAgents.Depth ?? 0,
                InheritConfig = false,
            };
        }

        // Problem 2: parse skills.auto_activate
        IReadOnlyList<string>? autoActivateSkills = dto.Runtime?.Skills?.AutoActivate;

        var definition = new AgentTemplateDefinition
        {
            Id = dto.Id,
            Name = dto.Name,
            Description = dto.Description,
            Version = dto.Version,
            SystemPrompt = dto.SystemPrompt,
            Model = dto.Model,
            Tools = toolsConfig,
            Permission = permission,
            Runtime = subAgents != null ? new TemplateRuntimeConfig { SubAgents = subAgents } : null,
        };

        int maxIterations = dto.Runtime?.MaxIterations ?? 20;
        int maxContextTokens = dto.Runtime?.MaxContextTokens ?? 80_000;

        return new LoadedTemplate(definition, maxIterations, maxContextTokens, autoActivateSkills);
    }
}

// ── Internal DTOs ─────────────────────────────────────────────────────────────
// Field names follow snake_case per the template spec.
// PropertyNameCaseInsensitive = true allows SYSTEM_PROMPT, System_Prompt etc.

internal sealed class AgentTemplateDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    // Problem 1: was "systemPrompt", now "system_prompt" (snake_case)
    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("tools")]
    public AgentTemplateDtoTools? Tools { get; init; }

    [JsonPropertyName("permission")]
    public AgentTemplateDtoPermission? Permission { get; init; }

    [JsonPropertyName("runtime")]
    public AgentTemplateDtoRuntime? Runtime { get; init; }
}

internal sealed class AgentTemplateDtoTools
{
    // Problem 1: was "allowAll", now "allow_all"
    [JsonPropertyName("allow_all")]
    public bool? AllowAll { get; init; }

    // Problem 1: was "allowedTools", now "allowed_tools"
    [JsonPropertyName("allowed_tools")]
    public IReadOnlyList<string>? AllowedTools { get; init; }
}

internal sealed class AgentTemplateDtoPermission
{
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    // Problem 1: was "requireApproval", now "require_approval"
    [JsonPropertyName("require_approval")]
    public IReadOnlyList<string>? RequireApproval { get; init; }

    [JsonPropertyName("deny")]
    public IReadOnlyList<string>? Deny { get; init; }
}

internal sealed class AgentTemplateDtoRuntime
{
    // Problem 1: was "maxIterations", now "max_iterations"
    [JsonPropertyName("max_iterations")]
    public int? MaxIterations { get; init; }

    [JsonPropertyName("max_context_tokens")]
    public int? MaxContextTokens { get; init; }

    // Problem 1: was "subAgents", now "sub_agents"
    [JsonPropertyName("sub_agents")]
    public AgentTemplateDtoSubAgents? SubAgents { get; init; }

    // Problem 2: skills support
    [JsonPropertyName("skills")]
    public AgentTemplateDtoSkills? Skills { get; init; }

    // Problem 6: todo support (low priority, parsed but not yet wired to AgentConfig)
    [JsonPropertyName("todo")]
    public AgentTemplateDtoTodo? Todo { get; init; }
}

internal sealed class AgentTemplateDtoSubAgents
{
    [JsonPropertyName("depth")]
    public int? Depth { get; init; }
}

// Problem 2: skills DTO
internal sealed class AgentTemplateDtoSkills
{
    // snake_case: "auto_activate"
    [JsonPropertyName("auto_activate")]
    public IReadOnlyList<string>? AutoActivate { get; init; }

    [JsonPropertyName("recommend")]
    public IReadOnlyList<string>? Recommend { get; init; }
}

// Problem 6: todo DTO
internal sealed class AgentTemplateDtoTodo
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }
}
