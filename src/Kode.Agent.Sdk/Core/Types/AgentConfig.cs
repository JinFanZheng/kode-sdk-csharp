namespace Kode.Agent.Sdk.Core.Types;

using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Hooks;
using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Core.Templates;

/// <summary>
/// Configuration for creating an agent.
/// </summary>
/// <example>
/// <code>
/// var config = new AgentConfig
/// {
///     Model = "claude-sonnet-4-6",
///     SystemPrompt = "You are a helpful assistant.",
///     MaxIterations = 20,
///     Tools = ["fs_read", "fs_write", "bash_run"],
///     Permissions = new PermissionConfig
///     {
///         Mode = "auto",
///         RequireApprovalTools = ["bash_run"],
///         DenyTools = ["fs_rm"]
///     }
/// };
/// config.Validate(); // opt-in: fail fast on bad numeric/time values
/// </code>
/// </example>
public record AgentConfig
{
  /// <summary>
  /// The model to use (e.g., "claude-3-5-sonnet-20241022").
  /// </summary>
  public string Model { get; init; } = string.Empty;

    /// <summary>
    /// System prompt/instructions.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Template ID for agent templates.
    /// </summary>
    public string? TemplateId { get; init; }

    /// <summary>
    /// Maximum iterations before stopping.
    /// </summary>
    public int MaxIterations { get; init; } = 100;

    /// <summary>
    /// Maximum tokens per model call.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Temperature for model sampling.
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// Whether to enable extended thinking.
    /// </summary>
    public bool EnableThinking { get; init; }

    /// <summary>
    /// Token budget for thinking.
    /// </summary>
    public int? ThinkingBudget { get; init; }

    /// <summary>
    /// Whether to expose thinking/reasoning content in events/messages (default: false, aligned with TS exposeThinking).
    /// </summary>
    public bool? ExposeThinking { get; init; }

    /// <summary>
    /// Tool names to enable.
    /// </summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>
    /// Permission mode configuration.
    /// </summary>
    public PermissionConfig? Permissions { get; init; }

    /// <summary>
    /// Sandbox options for the agent runtime.
    /// </summary>
    public SandboxOptions? SandboxOptions { get; init; }

    /// <summary>
    /// Optional hooks to run during agent execution.
    /// </summary>
    public IReadOnlyList<IHooks>? Hooks { get; init; }

  /// <summary>
  /// Optional context manager configuration (aligned with TS context options).
  /// </summary>
  public ContextManagerOptions? Context { get; init; }

    /// <summary>
    /// Optional skills configuration (aligned with TS config.skills).
    /// </summary>
    public SkillsConfig? Skills { get; init; }

    /// <summary>
    /// Optional sub-agent configuration (aligned with TS template.runtime.subagents / overrides.subagents).
    /// </summary>
    public SubAgentConfig? SubAgents { get; init; }

    /// <summary>
    /// Optional todo configuration (aligned with TS template.runtime.todo / overrides.todo).
    /// </summary>
    public TodoConfig? Todo { get; init; }

    /// <summary>
    /// Maximum tool concurrency.
    /// </summary>
    public int MaxToolConcurrency { get; init; } = 3;

    /// <summary>
    /// Tool execution timeout (default: 10min).
    /// CLI tools like claude -p may run 2-5 minutes for complex tasks;
    /// 60s hard-kill was too aggressive, causing premature cancellation.
    /// </summary>
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Free-form session type tag for observability. Emitted as a metric/activity tag on
    /// Token, ModelRequest, and Run to enable cost attribution. The SDK does not interpret
    /// the value — hosts choose whatever vocabulary fits (e.g. "main", "channel", "automation",
    /// "cli", "api"). Empty string disables the tag at the host's discretion.
    /// </summary>
    public string SessionType { get; init; } = "main";

    /// <summary>
    /// Free-form agent role tag for observability. Emitted as a metric/activity tag.
    /// The SDK's built-in SubAgentRunner sets this to "sub-agent" so orchestration
    /// sub-agent token costs are separable from the primary agent's; hosts may use
    /// any other value (e.g. "primary", "worker", "reviewer").
    /// </summary>
    public string AgentRole { get; init; } = "primary";

    /// <summary>
    /// Parent activity context for distributed tracing across sub-agents.
    /// When set by SubAgentRunner, the sub-agent's "agent.run" span becomes a child of the
    /// parent's "agent.tool.execute" span, forming a complete multi-agent call tree.
    /// </summary>
    public System.Diagnostics.ActivityContext ParentActivityContext { get; init; } = default;

    /// <summary>
    /// Validates the configuration and throws if any value is out of range.
    /// Opt-in: callers may invoke this before passing the config to Agent to fail fast
    /// with a precise error instead of surfacing a downstream symptom (deadlock, silent
    /// no-op, provider error). Not invoked automatically to preserve backward compatibility.
    /// </summary>
    /// <example>
    /// <code>
    /// var config = new AgentConfig { MaxIterations = 0 };
    /// config.Validate(); // throws ArgumentOutOfRangeException("MaxIterations", …)
    /// </code>
    /// </example>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a numeric/time field is out of range.</exception>
    public void Validate()
    {
        if (MaxIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxIterations), MaxIterations, "Must be ≥ 1.");

        if (MaxTokens is { } maxTok && maxTok < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxTokens), maxTok, "Must be ≥ 1 when set.");

        if (Temperature is { } temp && (temp < 0.0 || temp > 2.0))
            throw new ArgumentOutOfRangeException(nameof(Temperature), temp, "Must be in [0.0, 2.0] when set.");

        if (ThinkingBudget is { } thinking && thinking < 1)
            throw new ArgumentOutOfRangeException(nameof(ThinkingBudget), thinking, "Must be ≥ 1 when set.");

        if (MaxToolConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxToolConcurrency), MaxToolConcurrency, "Must be ≥ 1.");

        if (ToolTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ToolTimeout), ToolTimeout, "Must be > TimeSpan.Zero.");

        // SessionType / AgentRole are free-form observability tags; the SDK does not
        // prescribe a vocabulary, so they are not validated here.
    }
}

/// <summary>
/// Optional override values used when resuming from Store metadata (aligned with TS resumeFromStore overrides).
/// Any null property means "keep the stored value".
/// </summary>
public record AgentConfigOverrides
{
    public string? Model { get; init; }
    public string? SystemPrompt { get; init; }
    public string? TemplateId { get; init; }
    public int? MaxIterations { get; init; }
    public int? MaxTokens { get; init; }
    public double? Temperature { get; init; }
    public bool? EnableThinking { get; init; }
    public int? ThinkingBudget { get; init; }
    public bool? ExposeThinking { get; init; }
    public IReadOnlyList<string>? Tools { get; init; }
    public PermissionConfig? Permissions { get; init; }
    public SandboxOptions? SandboxOptions { get; init; }
    public IReadOnlyList<IHooks>? Hooks { get; init; }
    public ContextManagerOptions? Context { get; init; }
    public SkillsConfig? Skills { get; init; }
    public SubAgentConfig? SubAgents { get; init; }
    public TodoConfig? Todo { get; init; }
    public int? MaxToolConcurrency { get; init; }
    public TimeSpan? ToolTimeout { get; init; }
}

/// <summary>
/// Permission configuration for tool execution.
/// </summary>
public record PermissionConfig
{
    /// <summary>
    /// TS-aligned permission mode name.
    /// Built-in: "auto" | "approval" | "readonly". Custom modes are allowed.
    /// </summary>
    public string Mode { get; init; } = "auto";

    /// <summary>
    /// Optional allowlist of tools that are permitted to run.
    /// If specified (non-empty), any tool not in this list will be denied.
    /// Use "*" to allow all tools.
    /// </summary>
    public IReadOnlyList<string>? AllowTools { get; init; }

    /// <summary>
    /// Tools that require explicit approval (TS: requireApprovalTools).
    /// </summary>
    public IReadOnlyList<string>? RequireApprovalTools { get; init; }

    /// <summary>
    /// Tools that are always denied (TS: denyTools).
    /// </summary>
    public IReadOnlyList<string>? DenyTools { get; init; }

    /// <summary>
    /// Tools hidden from the model's tool schema until revealed via skill activation.
    /// Hidden tools are still registered and executable if explicitly granted,
    /// but their schema is not sent to the model until a skill calls GrantTools().
    /// </summary>
    public IReadOnlyList<string>? SchemaHiddenTools { get; init; }

    /// <summary>
    /// Optional metadata for custom permission modes.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// Options for resuming an agent.
/// </summary>
public record ResumeOptions
{
    /// <summary>
    /// Whether to auto-run after resuming.
    /// </summary>
    public bool AutoRun { get; init; }

    /// <summary>
    /// Recovery strategy for incomplete tool calls.
    /// </summary>
    public RecoveryStrategy Strategy { get; init; } = RecoveryStrategy.Crash;
}

/// <summary>
/// Strategy for recovering from incomplete state.
/// </summary>
public enum RecoveryStrategy
{
    /// <summary>
    /// Seal incomplete tool calls as failed.
    /// </summary>
    Crash,

    /// <summary>
    /// Keep state as-is for manual recovery.
    /// </summary>
    Manual
}
