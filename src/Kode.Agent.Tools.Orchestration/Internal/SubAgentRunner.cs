using System.Diagnostics;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Agent;
using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Types;
using AgentRuntime = Kode.Agent.Sdk.Core.Agent.Agent;

namespace Kode.Agent.Tools.Orchestration.Internal;

/// <summary>
/// Shared sub-agent spawn logic used by all orchestration tools.
/// Encapsulates tool whitelisting, sandbox inheritance, agent configuration, and result extraction.
/// </summary>
internal static class SubAgentRunner
{
    /// <summary>
    /// Tools that are safe to delegate by default — read-only, no side effects.
    /// </summary>
    internal static readonly IReadOnlyList<string> DefaultTools =
    [
        "fs_read", "fs_glob", "fs_grep", "fs_list",
        "bash_run", "bash_logs",
    ];

    /// <summary>
    /// Hard whitelist: channel/approval tools are always stripped.
    /// Sub-agents can read/write files and run shell commands.
    /// </summary>
    internal static readonly HashSet<string> AllowedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "fs_read", "fs_glob", "fs_grep", "fs_list",
        "fs_write", "fs_edit", "fs_rm",
        "bash_run", "bash_logs", "bash_kill",
        "todo_read", "todo_write",
    };

    /// <summary>
    /// Spawns an isolated sub-agent, runs the given task, and returns a summary.
    /// </summary>
    internal static async Task<SubAgentResult> RunAsync(
        SubAgentRequest request,
        CancellationToken cancellationToken)
    {
        // 1. Filter tools against the hard whitelist
        IReadOnlyList<string> tools;
        if (request.AllowNoTools && request.Tools is { Count: 0 })
        {
            // Explicit empty-tool request (e.g. context_distill, debate judge)
            tools = [];
        }
        else
        {
            var requestedTools = request.Tools ?? DefaultTools;
            var filtered = requestedTools
                .Where(t => AllowedTools.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (filtered.Count == 0)
                return SubAgentResult.Fail(
                    $"No allowed tools remain after filtering. Allowed: {string.Join(", ", AllowedTools)}");

            tools = filtered;
        }

        // 2. Build child sandbox — inherit parent's access rights
        var parentOpts = request.ParentSandboxOptions;
        var childWorkDir = request.WorkDir ?? parentOpts?.WorkingDirectory;
        var childSandboxOptions = new SandboxOptions
        {
            WorkingDirectory = childWorkDir,
            EnforceBoundary = parentOpts?.EnforceBoundary ?? true,
            AllowPaths = BuildAllowPaths(parentOpts, request.WorkDir),
            WatchFiles = false,
        };

        // 3. Determine iteration budget
        //    Auto mode: run a lightweight complexity estimator before the main task.
        //    Fixed mode: use the caller-supplied values directly.
        var maxIterations = Math.Clamp(request.MaxIterations, 1, 50);
        var maxContextTokens = Math.Max(20_000, request.MaxContextTokens);

        if (request.MaxIterationsMode == MaxIterationsMode.Auto)
        {
            var estimate = await ComplexityEstimator.EstimateAsync(
                task: request.Task,
                workDir: childWorkDir,
                modelId: request.ModelId,
                modelProvider: request.ModelProvider,
                toolRegistry: request.ToolRegistry,
                sandboxFactory: request.SandboxFactory,
                fallbackIterations: maxIterations,
                fallbackContextTokens: maxContextTokens,
                loggerFactory: request.LoggerFactory,
                cancellationToken: cancellationToken);
            maxIterations = estimate.MaxIterations;
            maxContextTokens = estimate.MaxContextTokens;
        }

        // 4. Build agent config
        var agentId = $"sub-{Guid.NewGuid():N}"[..24];
        var systemPrompt = request.SystemPromptOverride
                           ?? BuildSystemPrompt(childWorkDir);

        // OT-1B: capture the calling tool's Activity as parent context so the sub-agent
        // run span becomes a child of the spawning "agent.tool.execute" span in the trace.
        // OT-2A: set AgentRole = "sub-agent" to enable cost attribution in token metrics.
        var config = new AgentConfig
        {
            Model = request.ModelId,
            SystemPrompt = systemPrompt,
            MaxIterations = maxIterations,
            Tools = tools.Count > 0 ? tools : null,
            SandboxOptions = childSandboxOptions,
            Permissions = new PermissionConfig
            {
                Mode = "auto",
                RequireApprovalTools = [],
            },
            Context = new ContextManagerOptions
            {
                MaxTokens = maxContextTokens,
                CompressToTokens = (int)(maxContextTokens * 0.625),
                ToolResultCompression = new ToolResultCompressionOptions { Enabled = true },
            },
            AgentRole = "sub-agent",
            ParentActivityContext = Activity.Current?.Context ?? default,
        };

        var deps = new AgentDependencies
        {
            Store = new InMemoryAgentStore(),
            SandboxFactory = request.SandboxFactory,
            ToolRegistry = request.ToolRegistry,
            ModelProvider = request.ModelProvider,
            LoggerFactory = request.LoggerFactory,
        };

        // 5. Run sub-agent
        await using var agent = await AgentRuntime.CreateAsync(agentId, config, deps, cancellationToken);

        // 6. Notify parent and forward events (single-agent tools only — parallel tools opt out by not passing ParentEventBus)
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        request.ParentEventBus?.EmitMonitor(new SubAgentCreatedEvent
        {
            Type = "subagent.created",
            AgentId = agentId,
            TemplateId = request.Label ?? "sub-agent",
            ParentAgentId = "unknown",
            CallId = request.ToolCallId,
            Timestamp = now,
        });

        using var forwardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var eventForwardTask = ForwardEventsAsync(
            agent.EventBus,
            request.ParentEventBus,
            agentId,
            request.Label ?? "sub-agent",
            request.ToolCallId,
            forwardCts.Token);

        var result = await agent.RunAsync(request.Task, cancellationToken);

        await forwardCts.CancelAsync();
        try { await eventForwardTask; } catch (OperationCanceledException) { }

        if (!result.Success)
            return SubAgentResult.Fail(
                $"Sub-agent failed (stopReason={result.StopReason}): {result.Response}");

        return new SubAgentResult
        {
            Success = true,
            Summary = result.Response ?? "(no output)",
            StopReason = result.StopReason.ToString(),
            ToolsUsed = tools,
        };
    }

    // ── event forwarding ──────────────────────────────────────────────────────

    private static async Task ForwardEventsAsync(
        IEventBus subBus,
        IEventBus? parentBus,
        string subAgentId,
        string label,
        string? parentCallId,
        CancellationToken ct)
    {
        if (parentBus == null) return;

        try
        {
            await foreach (var envelope in subBus.SubscribeAsync(EventChannel.Progress, null, ct))
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                switch (envelope.Event)
                {
                    case TextChunkEvent chunk:
                        parentBus.EmitMonitor(new SubAgentDeltaEvent
                        {
                            Type = "subagent.delta",
                            SubAgentId = subAgentId,
                            TemplateId = label,
                            CallId = parentCallId,
                            Delta = chunk.Delta,
                            Text = chunk.Delta,
                            Step = chunk.Step,
                            Timestamp = ts,
                        });
                        break;

                    case ThinkChunkEvent think:
                        parentBus.EmitMonitor(new SubAgentThinkingEvent
                        {
                            Type = "subagent.thinking",
                            SubAgentId = subAgentId,
                            TemplateId = label,
                            CallId = parentCallId,
                            Delta = think.Delta,
                            Step = think.Step,
                            Timestamp = ts,
                        });
                        break;

                    case ToolStartEvent toolStart:
                        parentBus.EmitMonitor(new SubAgentToolStartEvent
                        {
                            Type = "subagent.tool_start",
                            SubAgentId = subAgentId,
                            TemplateId = label,
                            ParentCallId = parentCallId,
                            ToolCallId = toolStart.Call.Id,
                            ToolName = toolStart.Call.Name,
                            Timestamp = ts,
                        });
                        break;

                    case ToolEndEvent toolEnd:
                        parentBus.EmitMonitor(new SubAgentToolEndEvent
                        {
                            Type = "subagent.tool_end",
                            SubAgentId = subAgentId,
                            TemplateId = label,
                            ParentCallId = parentCallId,
                            ToolCallId = toolEnd.Call.Id,
                            ToolName = toolEnd.Call.Name,
                            Timestamp = ts,
                        });
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    internal static string BuildSystemPrompt(string? workDir)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(
            "You are a focused sub-agent. Your only job is to complete the given task " +
            "and produce a concise, factual summary of your findings or actions.");

        if (!string.IsNullOrWhiteSpace(workDir))
            sb.AppendLine($"Working directory: {workDir}");

        sb.AppendLine();
        sb.AppendLine("Guidelines:");
        sb.AppendLine("- Use available tools to gather information or perform the task. Be systematic.");
        sb.AppendLine("- Do NOT send messages, modify workspace files outside the task scope, or create approvals.");
        sb.AppendLine("- When you have enough information, write your final answer and STOP. Do not call any more tools after writing your final answer.");
        sb.AppendLine("- Keep the final answer under 500 words. Be specific and factual — return CONCLUSIONS and DECISIONS, not raw tool output.");
        sb.AppendLine("- Never paste long files, full command output, or bulk transcripts into the final answer. Cite file paths, line ranges, or key fragments instead — the caller can fs_read them directly if needed.");
        sb.AppendLine("- If something cannot be determined, say so explicitly rather than continuing to search.");

        return sb.ToString();
    }

    internal static IReadOnlyList<string>? BuildAllowPaths(SandboxOptions? parentOpts, string? extraWorkDir)
    {
        var paths = new List<string>();

        if (parentOpts?.AllowPaths is { Count: > 0 } existing)
            paths.AddRange(existing);

        if (!string.IsNullOrWhiteSpace(extraWorkDir)
            && !paths.Contains(extraWorkDir, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(extraWorkDir);
        }

        return paths.Count > 0 ? paths : null;
    }
}

/// <summary>
/// Parameters for a single sub-agent invocation.
/// Record type so orchestration tools can build a shared base and customize via <c>with</c>.
/// </summary>
internal sealed record SubAgentRequest
{
    public required string Task { get; init; }
    public string? WorkDir { get; init; }
    public IReadOnlyList<string>? Tools { get; init; }
    public int MaxIterations { get; init; } = 12;

    /// <summary>
    /// Maximum context tokens for the sub-agent. Compression triggers at 62.5% of this value.
    /// Defaults to 80 000. Increase for tasks that read many large files.
    /// </summary>
    public int MaxContextTokens { get; init; } = 80_000;

    /// <summary>
    /// When <see cref="MaxIterationsMode.Auto"/>, a lightweight complexity-estimator sub-agent
    /// runs first and overrides <see cref="MaxIterations"/> and <see cref="MaxContextTokens"/>.
    /// </summary>
    public MaxIterationsMode MaxIterationsMode { get; init; } = MaxIterationsMode.Fixed;

    public SandboxOptions? ParentSandboxOptions { get; init; }
    public required IModelProvider ModelProvider { get; init; }
    public required string ModelId { get; init; }
    public required IToolRegistry ToolRegistry { get; init; }
    public required ISandboxFactory SandboxFactory { get; init; }
    public Microsoft.Extensions.Logging.ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// When true, an explicitly empty <see cref="Tools"/> list is allowed —
    /// the sub-agent will respond using pure reasoning without calling any tools.
    /// Used by <c>context_distill</c>, <c>debate</c> judge/sides, etc.
    /// </summary>
    public bool AllowNoTools { get; init; } = false;

    /// <summary>
    /// If set, replaces the default system prompt entirely.
    /// Used by <c>ask_specialist</c> to inject a specialist role.
    /// </summary>
    public string? SystemPromptOverride { get; init; }

    /// <summary>
    /// When set, the sub-agent's Progress events are forwarded to this bus as Monitor events.
    /// Parallel tools (parallel_research, map_reduce, fan_out_fan_in) should NOT set this
    /// because N concurrent streams create interleaved noise with no clear ordering.
    /// </summary>
    public IEventBus? ParentEventBus { get; init; }

    /// <summary>
    /// Human-readable label for this sub-agent (used as TemplateId in forwarded events).
    /// Defaults to "sub-agent" when not provided.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The parent tool call ID, attached to forwarded events for correlation.
    /// </summary>
    public string? ToolCallId { get; init; }
}

/// <summary>
/// Result from a sub-agent invocation.
/// </summary>
internal sealed class SubAgentResult
{
    public bool Success { get; init; }
    public string? Summary { get; init; }
    public string? Error { get; init; }
    public string? StopReason { get; init; }
    public IReadOnlyList<string>? ToolsUsed { get; init; }

    internal static SubAgentResult Fail(string error) => new() { Success = false, Error = error };
}
