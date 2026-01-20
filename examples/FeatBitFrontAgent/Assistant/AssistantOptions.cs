using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Core.Types;

namespace Kode.Agent.WebApiAssistant.Assistant;

/// <summary>
/// Options for creating an Assistant agent.
/// </summary>
public record CreateAssistantOptions
{
    /// <summary>
    /// Working directory (default: current directory).
    /// </summary>
    public string? WorkDir { get; init; }

    /// <summary>
    /// Store directory for agent persistence (default: .assistant-store in workDir).
    /// </summary>
    public string? StoreDir { get; init; }

    /// <summary>
    /// Agent ID for resuming existing agents.
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// User ID for multi-user isolation.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// User data directory (takes priority over userId).
    /// </summary>
    public string? UserDataDir { get; init; }

    /// <summary>
    /// Optional model override.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Optional system prompt override.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Optional temperature override.
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// Optional max tokens override.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Optional skills configuration override.
    /// </summary>
    public SkillsConfig? Skills { get; init; }

    /// <summary>
    /// Optional permission configuration override.
    /// </summary>
    public PermissionConfig? Permissions { get; init; }
}
