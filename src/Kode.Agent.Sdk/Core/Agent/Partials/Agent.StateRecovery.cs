using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Events;
using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Core.Templates;
using System.Text.Json;

namespace Kode.Agent.Sdk.Core.Agent;

// Resume-from-store entry points + metadata-to-AgentConfig reconstruction.
// All members here are `static` — they read AgentInfo.Metadata / store state
// and hand off to ResumeFromStoreInternalAsync which owns the non-static side.
public sealed partial class Agent
{
    /// <summary>
    /// Resumes an agent from stored state.
    /// </summary>
    public static async Task<Agent> ResumeFromStoreAsync(
        string agentId,
        AgentDependencies dependencies,
        ResumeOptions? options = null,
        AgentConfigOverrides? overrides = null,
        CancellationToken cancellationToken = default)
    {
        var info = await dependencies.Store.LoadInfoAsync(agentId, cancellationToken);
        if (info?.Metadata == null)
        {
            throw new InvalidOperationException($"Agent metadata not found: {agentId}");
        }

        var baseConfig = BuildResumeConfigFromInfo(info);
        var toolDescriptors = ReadToolDescriptors(info);
        var merged = ApplyResumeOverrides(baseConfig, overrides);
        return await ResumeFromStoreInternalAsync(agentId, merged, dependencies, toolDescriptors, options, cancellationToken);
    }

    public static async Task<Agent> ResumeFromStoreAsync(
        string agentId,
        AgentConfig config,
        AgentDependencies dependencies,
        ResumeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await ResumeFromStoreInternalAsync(agentId, config, dependencies, null, options, cancellationToken);
    }

    private static async Task<Agent> ResumeFromStoreInternalAsync(
        string agentId,
        AgentConfig config,
        AgentDependencies dependencies,
        IReadOnlyList<ToolDescriptor>? toolDescriptors,
        ResumeOptions? options,
        CancellationToken cancellationToken)
    {
        var agent = new Agent(agentId, config, dependencies, toolDescriptors);
        agent._sandbox = await dependencies.SandboxFactory.CreateAsync(agent._config.SandboxOptions, cancellationToken);
        agent.InitializeToolServices();

        if (string.IsNullOrWhiteSpace(agent._config.Model))
        {
            throw new InvalidOperationException("AgentConfig.Model is required (can be provided via template merge)");
        }

        // Load messages from store
        var messages = await dependencies.Store.LoadMessagesAsync(agentId, cancellationToken);
        agent._messages.AddRange(messages);

        // One-shot cleanup for sessions persisted before MessageQueue DedupKey: collapse
        // runs of byte-identical system-reminder payloads (e.g. file-change bursts). Only
        // affects history that is provably redundant; if anything is collapsed we persist
        // immediately so the cleanup is durable across future resumes.
        var collapsed = agent.CollapseConsecutiveDuplicateReminders();
        if (collapsed > 0)
        {
            agent._eventBus.EmitMonitor(new AgentRecoveredEvent
            {
                Type = "agent_recovered",
                Reason = "reminder_dedup",
                Detail = new { removed = collapsed }
            });
            await agent.SaveStateAsync(cancellationToken);
        }

        // One-shot cleanup for sessions whose tool_results were persisted before the current
        // offload threshold took effect: bring legacy oversized ToolResultContent payloads in
        // line with the active policy. Guarded by (a) a configured non-lossy compressor and
        // (b) ToolResultCompression.Enabled — both are required for the live path too.
        var offloaded = await agent.OffloadLegacyOversizedToolResultsAsync(cancellationToken);
        if (offloaded.count > 0)
        {
            agent._eventBus.EmitMonitor(new AgentRecoveredEvent
            {
                Type = "agent_recovered",
                Reason = "legacy_tool_result_offload",
                Detail = new { count = offloaded.count, totalBytes = offloaded.bytes }
            });
            await agent.SaveStateAsync(cancellationToken);
        }

        // Load tool call records
        var toolRecords = await dependencies.Store.LoadToolCallRecordsAsync(agentId, cancellationToken);
        agent._toolRunner.LoadToolCallRecords(toolRecords);

        var sealedSnapshots = new List<ToolCallSnapshot>();

        // Handle recovery strategy
        if (options?.Strategy == RecoveryStrategy.Crash)
        {
            // TS-aligned: seal any non-terminal tool calls and append synthetic tool_result blocks for dangling tool_use.
            var terminal = new HashSet<ToolCallState>
            {
                ToolCallState.Completed,
                ToolCallState.Failed,
                ToolCallState.Denied,
                ToolCallState.Sealed
            };

            foreach (var record in agent._toolRunner.ActiveToolCalls)
            {
                if (terminal.Contains(record.State)) continue;

                var state = record.State switch
                {
                    ToolCallState.ApprovalRequired => "APPROVAL_REQUIRED",
                    ToolCallState.Approved => "APPROVED",
                    ToolCallState.Executing => "EXECUTING",
                    ToolCallState.Pending => "PENDING",
                    _ => record.State.ToString().ToUpperInvariant()
                };

                var payload = agent.BuildSealPayload(state, record.Id, "Sealed during crash recovery", record);
                agent._toolRunner.SealToolCall(record.Id, payload.Message, payload.Payload);
                var snapshot = agent._toolRunner.GetSnapshot(record.Id);
                if (snapshot != null) sealedSnapshots.Add(snapshot);
            }

            var dangling = await agent.AutoSealDanglingToolUsesAsync(
                "Sealed missing tool_result after crash-resume; verify potential side effects.",
                cancellationToken);
            sealedSnapshots.AddRange(dangling);

            await agent.SaveStateAsync(cancellationToken);
        }

        // Seed EventBus cursor/seq so Bookmark-based resume ("since") works across restarts.
        try
        {
            var info = await dependencies.Store.LoadInfoAsync(agentId, cancellationToken);
            if (info?.LastBookmark != null)
            {
                agent._eventBus.SeedFromBookmark(info.LastBookmark);
            }
            agent._lineage = info?.Lineage?.ToList() ?? [];
            agent._createdAt = info?.CreatedAt ?? agent._createdAt;
            if (info?.Breakpoint != null)
            {
                agent._breakpointManager.TransitionTo(info.Breakpoint.Value);
            }

            // Crash/restore: if we persisted AWAITING_APPROVAL but cannot reconstruct a pending approval, recover to READY.
            if (info?.Breakpoint == BreakpointState.AwaitingApproval)
            {
                var hasApprovalRequired = agent._toolRunner.ActiveToolCalls.Any(r =>
                    r.State == ToolCallState.ApprovalRequired &&
                    r.Approval.Required &&
                    string.IsNullOrWhiteSpace(r.Approval.Decision));
                if (!hasApprovalRequired)
                {
                    agent._eventBus.EmitMonitor(new AgentRecoveredEvent
                    {
                        Type = "agent_recovered",
                        Reason = "stale_awaiting_approval",
                        Detail = new { breakpoint = info.Breakpoint?.ToString() }
                    });
                    agent._breakpointManager.TransitionTo(BreakpointState.Ready);
                }
            }
        }
        catch
        {
            // best-effort: resume should work even if meta is missing
        }

        // Align TS stepCount semantics: seed from stored message history (user message count).
        agent._stepCount = agent._messages.Count(m => m.Role == MessageRole.User);
        agent._iterationCount = 0;

        await agent.InitializeSkillsAsync(cancellationToken);
        await agent.InitializeTodoAsync(cancellationToken);
        await agent.InitializeToolManualAsync(cancellationToken);

        agent._eventBus.EmitMonitor(new AgentResumedEvent
        {
            Type = "agent_resumed",
            Strategy = options?.Strategy == RecoveryStrategy.Crash ? "crash" : "manual",
            Sealed = sealedSnapshots
        });

        if (options?.AutoRun == true)
        {
            _ = agent.ResumeAsync(cancellationToken);
        }

        return agent;
    }

    private static AgentConfig BuildResumeConfigFromInfo(AgentInfo info)
    {
        var metadata = new Dictionary<string, JsonElement>(info.Metadata ?? new Dictionary<string, JsonElement>(), StringComparer.OrdinalIgnoreCase);

        var templateId = info.TemplateId ?? ReadString(metadata, "templateId");
        var model = ReadString(metadata, "model") ?? string.Empty;
        var systemPrompt = ReadString(metadata, "systemPrompt");

        var tools = ReadToolIds(metadata);
        var permissions = ReadObject<Kode.Agent.Sdk.Core.Types.PermissionConfig>(metadata, "permission");
        var todo = ReadObject<TodoConfig>(metadata, "todo");
        var subagents = ReadObject<SubAgentConfig>(metadata, "subagents");
        var context = ReadObject<ContextManagerOptions>(metadata, "context");
        var skills = ReadObject<SkillsConfig>(metadata, "skills");

        var sandboxOptions =
            ReadObject<SandboxOptions>(metadata, "sandboxOptions")
            ?? ReadSandboxOptionsFromSandboxConfig(metadata);

        var exposeThinking = ReadBool(metadata, "exposeThinking");
        var maxIterations = ReadInt(metadata, "maxIterations") ?? 100;
        var maxTokens = ReadInt(metadata, "maxTokens");
        var temperature = ReadDouble(metadata, "temperature");
        var enableThinking = ReadBool(metadata, "enableThinking") ?? false;
        var thinkingBudget = ReadInt(metadata, "thinkingBudget");
        var maxToolConcurrency = ReadInt(metadata, "maxToolConcurrency") ?? 3;
        var toolTimeoutMs = ReadInt(metadata, "toolTimeoutMs") ?? 600_000; // 10min default for back-compat, see AgentConfig.ToolTimeout

        return new AgentConfig
        {
            TemplateId = templateId,
            Model = model,
            SystemPrompt = systemPrompt,
            Tools = tools,
            Permissions = permissions,
            SandboxOptions = sandboxOptions,
            ExposeThinking = exposeThinking,
            MaxIterations = maxIterations > 0 ? maxIterations : 100,
            MaxTokens = maxTokens,
            Temperature = temperature,
            EnableThinking = enableThinking,
            ThinkingBudget = thinkingBudget,
            Context = context,
            Skills = skills,
            SubAgents = subagents,
            Todo = todo,
            MaxToolConcurrency = maxToolConcurrency > 0 ? maxToolConcurrency : 3,
            ToolTimeout = TimeSpan.FromMilliseconds(toolTimeoutMs > 0 ? toolTimeoutMs : 600_000) // 10min default
        };
    }

    private static AgentConfig ApplyResumeOverrides(AgentConfig config, AgentConfigOverrides? overrides)
    {
        if (overrides == null) return config;

        return config with
        {
            TemplateId = overrides.TemplateId ?? config.TemplateId,
            Model = overrides.Model ?? config.Model,
            SystemPrompt = overrides.SystemPrompt ?? config.SystemPrompt,
            Tools = overrides.Tools ?? config.Tools,
            Permissions = overrides.Permissions ?? config.Permissions,
            SandboxOptions = overrides.SandboxOptions ?? config.SandboxOptions,
            Hooks = overrides.Hooks ?? config.Hooks,
            MaxIterations = overrides.MaxIterations ?? config.MaxIterations,
            MaxTokens = overrides.MaxTokens ?? config.MaxTokens,
            Temperature = overrides.Temperature ?? config.Temperature,
            EnableThinking = overrides.EnableThinking ?? config.EnableThinking,
            ThinkingBudget = overrides.ThinkingBudget ?? config.ThinkingBudget,
            ExposeThinking = overrides.ExposeThinking ?? config.ExposeThinking,
            Context = overrides.Context ?? config.Context,
            Skills = overrides.Skills ?? config.Skills,
            SubAgents = overrides.SubAgents ?? config.SubAgents,
            Todo = overrides.Todo ?? config.Todo,
            MaxToolConcurrency = overrides.MaxToolConcurrency ?? config.MaxToolConcurrency,
            ToolTimeout = overrides.ToolTimeout ?? config.ToolTimeout
        };
    }

    private static string? ReadString(IReadOnlyDictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value)) return null;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    }

    private static int? ReadInt(IReadOnlyDictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)) return i;
        return null;
    }

    private static double? ReadDouble(IReadOnlyDictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)) return d;
        return null;
    }

    private static T? ReadObject<T>(IReadOnlyDictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value)) return default;
        if (value.ValueKind == JsonValueKind.Null) return default;
        if (value.ValueKind != JsonValueKind.Object && value.ValueKind != JsonValueKind.Array) return default;

        try
        {
            return value.Deserialize<T>(MetaJsonOptions);
        }
        catch
        {
            try
            {
                return value.Deserialize<T>();
            }
            catch
            {
                return default;
            }
        }
    }

    private static IReadOnlyList<string>? ReadToolIds(IReadOnlyDictionary<string, JsonElement> metadata)
    {
        // Preferred TS-aligned shape: metadata.tools = ToolDescriptor[]
        if (metadata.TryGetValue("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in tools.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                    continue;
                }

                if (item.ValueKind == JsonValueKind.Object)
                {
                    if ((item.TryGetProperty("registryId", out var registryId) || item.TryGetProperty("RegistryId", out registryId)) &&
                        registryId.ValueKind == JsonValueKind.String)
                    {
                        var id = registryId.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) list.Add(id);
                        continue;
                    }

                    if ((item.TryGetProperty("name", out var name) || item.TryGetProperty("Name", out name)) &&
                        name.ValueKind == JsonValueKind.String)
                    {
                        var n = name.GetString();
                        if (!string.IsNullOrWhiteSpace(n)) list.Add(n);
                        continue;
                    }
                }
            }

            if (list.Count > 0) return list;
        }

        // Back-compat: metadata.toolIds = string[]
        if (metadata.TryGetValue("toolIds", out var toolIds) && toolIds.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in toolIds.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }

            if (list.Count > 0) return list;
        }

        return null;
    }

    private static SandboxOptions? ReadSandboxOptionsFromSandboxConfig(IReadOnlyDictionary<string, JsonElement> metadata)
    {
        if (!metadata.TryGetValue("sandboxConfig", out var sandboxConfig) || sandboxConfig.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in sandboxConfig.EnumerateObject())
        {
            dict[prop.Name] = prop.Value;
        }

        return ConvertSandboxOptions(dict);
    }

    private static IReadOnlyList<ToolDescriptor>? ReadToolDescriptors(AgentInfo info)
    {
        if (info.Metadata == null) return null;
        var metadata = new Dictionary<string, JsonElement>(info.Metadata, StringComparer.OrdinalIgnoreCase);

        // TS-aligned: metadata.tools is a ToolDescriptor[]
        var tools = ReadObject<List<ToolDescriptor>>(metadata, "tools");
        if (tools is { Count: > 0 })
        {
            return tools;
        }

        return null;
    }
}
