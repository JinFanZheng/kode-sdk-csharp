using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Agent;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Infrastructure.Sandbox;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Store.Json;
using KodaClaw.Contracts;
using KodaClaw.Runtime.Diagnostics;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Runtime;

public sealed class MainSessionOptions
{
    public static readonly IReadOnlyList<string> DefaultTools =
    [
        "fs_read",
        "fs_write",
        "fs_glob",
        "fs_grep",
        "fs_edit",
        "fs_rm",
        "fs_list",
        "bash_run",
        "bash_kill",
        "bash_logs",
        "todo_read",
        "todo_write",
        "skill_list",
        "skill_activate",
        "skill_resource",
        "workspace_memory_append",
        "workspace_protocol_update",
        "config_update",
        "canvas_upsert",
        "inbox_create",
        "inbox_read",
        "workspace_read",
        "channel_send",
        "channel_list",
        "diagnostics_query",
        "schedule_reminder",
        "history_search",
        "isolate_task",
        "pipeline",
        "parallel_research",
        "retry_with_reflection",
        "ask_specialist",
        "context_distill",
        "validate_and_fix",
        "spawn_agent",
        // "browser_action"
        // fan_out_fan_in, map_reduce, debate — gated behind koda-orchestration skill
    ];

    public static readonly IReadOnlyList<string> DefaultRequireApprovalTools =
    [
        // "fs_write",
        "fs_edit",
        "fs_rm",
        "bash_run",
        "bash_kill",
        // "todo_write",
        // "skill_activate",
    ];

    public string Model { get; init; } = "koda-main";

    public string? SystemPrompt { get; init; } = "You are KodaClaw main assistant.";

    public int MaxIterations { get; init; } = 30;

    public int MaxPromptCharacters { get; init; } = 16000;

    public IReadOnlyList<string> Tools { get; init; } = DefaultTools;

    public PermissionConfig Permissions { get; init; } = new()
    {
        Mode = "auto",
        RequireApprovalTools = DefaultRequireApprovalTools,
    };

    /// <summary>
    /// Fraction of the model's context window at which compression is triggered.
    /// </summary>
    public double ContextCompressionTriggerRatio { get; init; } = 0.60;

    /// <summary>
    /// Fraction of the model's context window to compress down to.
    /// </summary>
    public double ContextCompressionTargetRatio { get; init; } = 0.40;

    /// <summary>
    /// Assumed context window size (tokens) used to compute compression thresholds.
    /// Conservative default of 128k covers all modern OpenAI and Anthropic models.
    /// </summary>
    public int DefaultContextWindowSize { get; init; } = 128_000;

    /// <summary>
    /// Custom system prompt for context compression.
    /// Empty string (default) uses the LlmContextSummarizer built-in prompt, which focuses on
    /// task objective, completed steps, file paths, key decisions, and remaining work.
    /// <br/>
    /// For chat sessions the default prompt works well out of the box.
    /// Override only when you need domain-specific compression behavior (e.g., a customer-support
    /// product may want to emphasise ticket IDs and resolution status over file paths).
    /// </summary>
    public string CompressionPrompt { get; init; } = "";
}

public interface IMainSessionAgentDependenciesFactory
{
    AgentDependencies Create(string sessionId, string sessionDirectory);
}

public sealed class MainSessionDependencies
{
    public required IModelProvider ModelProvider { get; init; }

    public IToolRegistry? ToolRegistry { get; init; }

    public ISandboxFactory? SandboxFactory { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Workspace root used to locate the per-agent artifacts directory for
    /// the file-backed tool-result compressor. When null, file-backed
    /// compression is disabled (the SDK falls back to its built-in LLM
    /// compressor if <see cref="ToolResultCompressionOptions.Enabled"/> is true).
    /// </summary>
    public string? WorkspaceRootPath { get; init; }

    /// <summary>
    /// Optional diagnostics sink the compressor uses to emit
    /// <c>tool_result.offloaded</c> events.
    /// </summary>
    public IDiagnosticsService? DiagnosticsService { get; init; }
}

public sealed class DefaultMainSessionAgentDependenciesFactory : IMainSessionAgentDependenciesFactory
{
    private readonly MainSessionDependencies _dependencies;

    public DefaultMainSessionAgentDependenciesFactory(MainSessionDependencies dependencies)
    {
        _dependencies = dependencies;
    }

    public AgentDependencies Create(string sessionId, string sessionDirectory)
    {
        var sessionsRoot = Directory.GetParent(sessionDirectory)?.FullName ?? sessionDirectory;

        IToolResultCompressor? compressor = null;
        if (!string.IsNullOrWhiteSpace(_dependencies.WorkspaceRootPath))
        {
            var artifactStore = new KodaClawArtifactStore(
                _dependencies.WorkspaceRootPath,
                _dependencies.DiagnosticsService,
                _dependencies.LoggerFactory?.CreateLogger<KodaClawArtifactStore>());
            compressor = new FileBackedToolResultCompressor(
                artifactStore,
                sessionId,
                _dependencies.LoggerFactory?.CreateLogger<FileBackedToolResultCompressor>());
        }

        return new AgentDependencies
        {
            Store = new JsonAgentStore(sessionsRoot),
            ToolRegistry = _dependencies.ToolRegistry ?? new ToolRegistry(),
            SandboxFactory = _dependencies.SandboxFactory ?? new LocalSandboxFactory(),
            ModelProvider = _dependencies.ModelProvider,
            LoggerFactory = _dependencies.LoggerFactory,
            ToolResultCompressor = compressor,
        };
    }
}
