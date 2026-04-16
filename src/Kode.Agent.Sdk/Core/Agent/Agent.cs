using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Events;
using Kode.Agent.Sdk.Core.Files;
using Kode.Agent.Sdk.Core.Hooks;
using Kode.Agent.Sdk.Core.Scheduling;
using Kode.Agent.Sdk.Core.Todo;
using Kode.Agent.Sdk.Core.Templates;
using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Diagnostics;
using Kode.Agent.Sdk.Infrastructure.Providers;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Kode.Agent.Sdk.Core.Agent;

/// <summary>
/// Core agent implementation with event-driven architecture.
/// </summary>
public sealed class Agent : IAgent, ISkillsAwareAgent, ITaskDelegatorAgent, ISubAgentSpawnerAgent
{
    private static readonly JsonSerializerOptions MetaJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AgentConfig _config;
    private readonly AgentDependencies _dependencies;
    private readonly ILogger<Agent>? _logger;
    private string _createdAt;

    private readonly HookManager _hookManager;
    private readonly EventBus _eventBus;
    private readonly BreakpointManager _breakpointManager;
    private readonly Scheduler _scheduler;
    private readonly PermissionManager _permissionManager;
    private readonly ToolRunner _toolRunner;
    private readonly MessageQueue _messageQueue;
    private readonly ContextManager _contextManager;
    private readonly IToolResultCompressor? _toolResultCompressor;
    private readonly List<Message> _messages = [];
    private readonly List<ITool> _tools = [];
    private readonly IReadOnlyList<ToolDescriptor>? _persistedToolDescriptors;
    private FilePool? _filePool;
    private IServiceProvider? _toolServices;
    private SkillsManager? _skillsManager;
    private string? _systemPrompt;
    private List<string> _lineage = [];
    private TodoService? _todoService;
    private TodoManager? _todoManager;
    private readonly Dictionary<string, JsonElement> _metadata = new(StringComparer.OrdinalIgnoreCase);

    private string _invalidToolArgsLastTool = "";
    private int _invalidToolArgsStreak;
    private string? _nextModelNudgeText;
    private NextModelToolsOverride? _nextModelToolsOverride;
    private AgentRunOptions? _currentRunOptions;
    // Loop detection: tracks (tool_name:input_hash) -> call count within the current run.
    private readonly Dictionary<string, int> _toolCallFingerprints = new(StringComparer.Ordinal);

    private ISandbox? _sandbox;
    private AgentRuntimeState _runtimeState = AgentRuntimeState.Ready;
    private volatile int _stepCount;
    private int _iterationCount;
    private int _interrupted;
    private readonly object _stateLock = new();
    private CancellationTokenSource? _runCts;
    private readonly object _processingLock = new();
    private Task? _processingTask;
    private CancellationTokenSource? _processingCts;
    private bool _processingQueued;
    private long _processingRunId;
    private long _lastProcessingHeartbeatMs;
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);
    private readonly object _activeToolCallsLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _activeToolCalls = new(StringComparer.Ordinal);
    private long _turnStartedAtMs;   // Unix ms when Working began; 0 = no active turn
    private long _lastActivityAtMs;  // Unix ms when last turn finished; 0 = never

    public string AgentId { get; }
    public AgentRuntimeState RuntimeState => _runtimeState;
    public BreakpointState BreakpointState => _breakpointManager.State;
    public int StepCount => _stepCount;
    public IEventBus EventBus => _eventBus;
    public SkillsManager? SkillsManager => _skillsManager;

    // Extended status properties — all lock-free / Interlocked-safe for /status reads
    public int MessageCount => _messages.Count;
    public int PendingQueueCount => _messageQueue.PendingCount;
    public int IterationCount => _iterationCount;
    public int MaxIterations => _config.MaxIterations;
    public DateTimeOffset? TurnStartedAt
    {
        get
        {
            var ms = Interlocked.Read(ref _turnStartedAtMs);
            return ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        }
    }
    public DateTimeOffset? LastActivityAt
    {
        get
        {
            var ms = Interlocked.Read(ref _lastActivityAtMs);
            return ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        }
    }
    public string? CurrentExecutingToolName => _toolRunner.ActiveToolCalls
        .FirstOrDefault(tc => tc.State == ToolCallState.Executing)?.Name;

    private Agent(
        string agentId,
        AgentConfig config,
        AgentDependencies dependencies,
        IReadOnlyList<ToolDescriptor>? persistedToolDescriptors = null)
    {
        AgentId = agentId;
        _config = ApplyTemplateConfig(config, dependencies);
        _dependencies = dependencies;
        _logger = dependencies.LoggerFactory?.CreateLogger<Agent>();
        _persistedToolDescriptors = persistedToolDescriptors;
        _createdAt = DateTimeOffset.UtcNow.ToString("O");

        _eventBus = new EventBus(dependencies.Store, agentId, dependencies.LoggerFactory?.CreateLogger<EventBus>());
        _breakpointManager = new BreakpointManager(_eventBus);
        _scheduler = new Scheduler(new SchedulerOptions
        {
            OnTrigger = info =>
            {
                var kind = info.Kind switch
                {
                    TriggerKind.Steps => "steps",
                    TriggerKind.Time => "time",
                    TriggerKind.Cron => "cron",
                    _ => "time"
                };

                _eventBus.EmitMonitor(new SchedulerTriggeredEvent
                {
                    Type = "scheduler_triggered",
                    TaskId = info.TaskId,
                    Spec = info.Spec,
                    Kind = kind,
                    TriggeredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }
        });
        _messageQueue = new MessageQueue(new MessageQueueOptions
        {
            WrapReminder = (content, options) => WrapReminder(content, options?.SkipStandardEnding ?? false),
            AddMessageAsync = EnqueueMessageAsync,
            PersistAsync = SaveStateAsync,
            EnsureProcessing = EnsureProcessing
        });

        _hookManager = new HookManager();
        RegisterHooks(_hookManager, _config, dependencies);

        IContextSummarizer? summarizer = dependencies.ModelProvider != null
            ? new LlmContextSummarizer(
                dependencies.ModelProvider,
                _config.Model,
                dependencies.LoggerFactory?.CreateLogger<LlmContextSummarizer>())
            : null;

        _toolResultCompressor = dependencies.ModelProvider != null
                                && _config.Context?.ToolResultCompression?.Enabled == true
            ? new LlmToolResultCompressor(
                dependencies.ModelProvider,
                _config.Model,
                dependencies.LoggerFactory?.CreateLogger<LlmToolResultCompressor>())
            : null;

        _contextManager = new ContextManager(
            dependencies.Store,
            agentId,
            _config.Context,
            summarizer,
            dependencies.LoggerFactory?.CreateLogger<ContextManager>());

        _systemPrompt = _config.SystemPrompt;

        // Load tools
        LoadTools();

        _toolRunner = new ToolRunner(_dependencies.ToolRegistry, _config.MaxToolConcurrency);
        _permissionManager = new PermissionManager(
            _eventBus,
            _config.Permissions,
            _tools.Select(t => t.ToDescriptor()).ToList(),
            _toolRunner,
            SaveStateAsync);
    }

    /// <summary>
    /// Creates a new agent instance.
    /// </summary>
    public static async Task<Agent> CreateAsync(
        string agentId,
        AgentConfig config,
        AgentDependencies dependencies,
        CancellationToken cancellationToken = default)
    {
        var agent = new Agent(agentId, config, dependencies);
        agent._sandbox = await dependencies.SandboxFactory.CreateAsync(agent._config.SandboxOptions, cancellationToken);
        agent.InitializeToolServices();
        await agent.InitializeTodoAsync(cancellationToken);
        await agent.InitializeSkillsAsync(cancellationToken);
        await agent.InitializeToolManualAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(agent._config.Model))
        {
            throw new InvalidOperationException("AgentConfig.Model is required (can be provided via template merge)");
        }

        // Persist initial meta so the session becomes resumable immediately.
        // Without this, the store directory can exist (events/runtime files) but meta.json may be missing,
        // and resume attempts will fail with "Agent metadata not found".
        await agent.UpdateInfoAsync(cancellationToken);

        return agent;
    }

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

    /// <inheritdoc />
        public async Task<AgentRunResult> RunAsync(string input, CancellationToken cancellationToken = default)
    {
        return await RunMultimodalAsync(
            [new TextContent { Text = input }],
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AgentRunResult> RunAsync(string input, AgentRunOptions? options, CancellationToken cancellationToken = default)
    {
        _currentRunOptions = options;
        try
        {
            return await RunMultimodalAsync([new TextContent { Text = input }], cancellationToken);
        }
        finally
        {
            _currentRunOptions = null;
        }
    }

    public async Task<AgentRunResult> RunAsync(IReadOnlyList<ContentBlock> parts, CancellationToken cancellationToken = default)
    {
        return await RunMultimodalAsync(parts, cancellationToken);
    }

    private async Task<AgentRunResult> RunMultimodalAsync(IReadOnlyList<ContentBlock> parts, CancellationToken cancellationToken = default)
    {
        // OT-1B: propagate parent trace context so sub-agent spans are children of the spawning tool span.
        var parentCtx = _config.ParentActivityContext != default ? _config.ParentActivityContext : default(ActivityContext?);
        using var runActivity = KodeAgentActivitySource.Source.StartActivity(
            "agent.run",
            ActivityKind.Internal,
            parentCtx ?? default);
        runActivity?.SetTag("agent.model", _config.Model);
        runActivity?.SetTag("agent.max_iterations", _config.MaxIterations);
        runActivity?.SetTag("agent.session_type", _config.SessionType);
        runActivity?.SetTag("agent.role", _config.AgentRole);
        // OT-2A: tag RunsStarted with session/role dimensions for cost attribution.
        var runDimensions = new TagList
        {
            { "model", _config.Model },
            { "session_type", _config.SessionType },
            { "agent_role", _config.AgentRole }
        };
        KodeAgentMetrics.RunsStarted.Add(1, runDimensions);
        var runStopwatch = Stopwatch.StartNew();

        TransitionState(AgentRuntimeState.Working);
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _messageQueue.Send(parts, new SendOptions { Kind = PendingKind.User });
            await _messageQueue.FlushAsync(_runCts.Token);

            string? finalResponse = null;
            StopReason stopReason = StopReason.EndTurn;
            var totalUsage = new TokenUsage { InputTokens = 0, OutputTokens = 0 };

            while (true)
            {
                _runCts.Token.ThrowIfCancellationRequested();

                if (_runtimeState == AgentRuntimeState.Paused)
                {
                    stopReason = StopReason.AwaitingApproval;
                    break;
                }

                var stepResult = await StepAsync(_runCts.Token);

                if (!stepResult.HasMoreSteps)
                {
                    // Get final text response
                    var lastMessage = _messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
                    finalResponse = lastMessage?.Content
                        .OfType<TextContent>()
                        .LastOrDefault()?.Text;
                    break;
                }
            }

            if (_iterationCount >= _config.MaxIterations)
            {
                stopReason = StopReason.MaxIterations;
            }

            runStopwatch.Stop();
            KodeAgentMetrics.RunsCompleted.Add(1, runDimensions);
            KodeAgentMetrics.RunDuration.Record(runStopwatch.Elapsed.TotalMilliseconds, runDimensions);
            runActivity?.SetTag("agent.stop_reason", stopReason.ToString());
            runActivity?.SetTag("agent.tokens.total", totalUsage.InputTokens + totalUsage.OutputTokens);

            return new AgentRunResult
            {
                Success = stopReason == StopReason.EndTurn,
                Response = finalResponse,
                StopReason = stopReason,
                TokenUsage = totalUsage
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            runStopwatch.Stop();
            KodeAgentMetrics.RunDuration.Record(runStopwatch.Elapsed.TotalMilliseconds, runDimensions);
            runActivity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

            // User-requested cancellation — silent stop, no error event.
            return new AgentRunResult
            {
                Success = false,
                StopReason = StopReason.Cancelled
            };
        }
        catch (OperationCanceledException oce)
        {
            KodeAgentMetrics.ModelErrors.Add(1, new KeyValuePair<string, object?>("model", _config.Model));
            runActivity?.SetStatus(ActivityStatusCode.Error, oce.Message);
            runActivity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", oce.GetType().FullName },
                { "exception.message", oce.Message }
            }));

            // Internal cancellation not triggered by the caller — most likely an HttpClient
            // request timeout (default was 100 s before we set InfiniteTimeSpan).  Treat as
            // an error so the user gets feedback instead of a silent hang.
            var message = oce is TaskCanceledException
                ? "Model request timed out. The context may be too large or the endpoint is slow."
                : oce.Message;
            _logger?.LogWarning(oce, "Agent run interrupted by internal cancellation (possible model timeout)");
            _eventBus.EmitMonitor(new ErrorEvent
            {
                Type = "error",
                Severity = "error",
                Phase = "model",
                Message = message,
                Detail = new { hint = "model_timeout", originalMessage = oce.Message }
            });
            return new AgentRunResult
            {
                Success = false,
                StopReason = StopReason.Error,
                ErrorMessage = message
            };
        }
        catch (Exception ex)
        {
            KodeAgentMetrics.ModelErrors.Add(1, new KeyValuePair<string, object?>("model", _config.Model));
            runActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            runActivity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message }
            }));

            _logger?.LogError(ex, "Error during agent run");

            _eventBus.EmitMonitor(new ErrorEvent
            {
                Type = "error",
                Severity = "error",
                Phase = "model",
                Message = ex.Message,
                Detail = new { stack = ex.StackTrace }
            });

            return new AgentRunResult
            {
                Success = false,
                StopReason = StopReason.Error,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            // Mirror the interrupt path (line ~933): reset both runtime state AND breakpoint state
            // so that a failed run never leaves the agent stuck at StreamingModel / PreModel.
            TransitionState(AgentRuntimeState.Ready);
            _breakpointManager.TransitionTo(BreakpointState.Ready);
            await SaveStateAsync();
        }
    }

    /// <summary>
    /// TS-aligned: enqueue a user/reminder message without blocking on completion.
    /// </summary>
    public string Send(string text, SendOptions? options = null) => _messageQueue.Send(text, options);

    /// <summary>
    /// Enqueue a multi-modal user message (text + images).
    /// </summary>
    public string Send(IReadOnlyList<Kode.Agent.Sdk.Core.Types.ContentBlock> parts, SendOptions? options = null)
        => _messageQueue.Send(parts, options);

    /// <summary>
    /// TS-aligned: returns the scheduler instance (equivalent to TS <c>agent.schedule()</c>).
    /// </summary>
    public Scheduler Schedule() => _scheduler;

    public sealed record SubscribeOptions
    {
        public Bookmark? Since { get; init; }
        public IReadOnlyCollection<string>? Kinds { get; init; }
    }

    /// <summary>
    /// TS-aligned: subscribe to progress/control/monitor event envelopes.
    /// Note: when <c>opts.since</c> is null, this method does NOT replay history (matches TS EventBus.subscribe()).
    /// </summary>
    public IAsyncEnumerable<EventEnvelope> Subscribe(
        IReadOnlyList<string>? channels = null,
        SubscribeOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        var flags = ParseChannels(channels);
        // TS: no replay unless `since` is explicitly provided.
        var since = opts?.Since;
        return _eventBus.SubscribeAsync(flags, since, opts?.Kinds, cancellationToken);
    }

    /// <summary>
    /// TS-aligned: subscribe to progress event envelopes only.
    /// Note: when <c>opts.since</c> is null, this method does NOT replay history.
    /// </summary>
    public IAsyncEnumerable<EventEnvelope<ProgressEvent>> SubscribeProgress(
        SubscribeOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        // TS: no replay unless `since` is explicitly provided.
        return _eventBus.SubscribeProgressAsync(opts?.Since, opts?.Kinds, cancellationToken);
    }

    /// <summary>
    /// TS-aligned: subscribe to a single control/monitor event by <c>type</c> (equivalent to TS <c>agent.on(type, handler)</c>).
    /// </summary>
    public IDisposable On(string eventType, Action<AgentEvent> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(handler);

        var channels = eventType is "permission_required" or "permission_decided"
            ? EventChannel.Control
            : EventChannel.Monitor;

        var cts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in _eventBus.SubscribeAsync(
                    channels,
                    since: null,
                    kinds: new[] { eventType },
                    cancellationToken: cts.Token))
                {
                    handler(envelope.Event);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on dispose
            }
            catch (ObjectDisposedException)
            {
                // agent shutting down
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unhandled exception in Agent.On subscription loop for {EventType}", eventType);
            }
        }, cts.Token);

        return new CancellationDisposable(cts);
    }

    /// <summary>
    /// TS-aligned: force the agent to (re)enter the processing loop.
    /// </summary>
    public void Kick()
    {
        if (_runtimeState == AgentRuntimeState.Paused) return;
        EnsureProcessing();
    }

    /// <summary>
    /// TS-aligned: interrupt current processing (best-effort), cancel active tool executions, and seal any dangling tool_use blocks.
    /// </summary>
    public async Task InterruptAsync(string? note = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _interrupted, 1);

        try
        {
            SealNonTerminalToolRecords(note ?? "Interrupted by user");
        }
        catch
        {
            // ignore best-effort sealing
        }

        // Cancel in-flight processing and tools (best-effort).
        lock (_processingLock)
        {
            _processingCts?.Cancel();
        }
        _runCts?.Cancel();

        List<CancellationTokenSource> active;
        lock (_activeToolCallsLock)
        {
            active = _activeToolCalls.Values.ToList();
        }
        foreach (var cts in active)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // ignore best-effort cancellation
            }
        }

        try
        {
            await AutoSealDanglingToolUsesAsync(note ?? "Interrupted by user", cancellationToken);
            await SaveStateAsync(cancellationToken);
        }
        catch
        {
            // best-effort sealing; ignore
        }

        TransitionState(AgentRuntimeState.Ready);
        _breakpointManager.TransitionTo(BreakpointState.Ready);
    }

    /// <summary>
    /// TS-aligned: returns a lightweight runtime status snapshot (equivalent to TS <c>agent.status()</c>).
    /// </summary>
    public Task<AgentStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new AgentStatus
        {
            AgentId = AgentId,
            State = _runtimeState,
            StepCount = _stepCount,
            LastSfpIndex = FindLastSfpIndex(),
            LastBookmark = _eventBus.LastBookmark,
            Cursor = _eventBus.GetCursor(),
            Breakpoint = _breakpointManager.State
        });
    }

    /// <summary>
    /// TS-aligned: returns the agent meta snapshot (equivalent to TS <c>agent.info()</c>).
    /// </summary>
    public Task<AgentInfo> InfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var info = new AgentInfo
        {
            AgentId = AgentId,
            TemplateId = _config.TemplateId,
            CreatedAt = _createdAt,
            Lineage = _lineage,
            ConfigVersion = typeof(Agent).Assembly.GetName().Version?.ToString(),
            MessageCount = _messages.Count,
            LastSfpIndex = FindLastSfpIndex(),
            LastBookmark = _eventBus.LastBookmark,
            Breakpoint = _breakpointManager.State,
            Metadata = BuildAgentMetadata(existing: null)
        };

        return Task.FromResult(info);
    }

    private sealed class CancellationDisposable : IDisposable
    {
        private CancellationTokenSource? _cts;

        public CancellationDisposable(CancellationTokenSource cts)
        {
            _cts = cts;
        }

        public void Dispose()
        {
            var cts = Interlocked.Exchange(ref _cts, null);
            if (cts == null) return;
            try
            {
                cts.Cancel();
            }
            catch
            {
                // ignore
            }
            cts.Dispose();
        }
    }

    public sealed record StreamOptions
    {
        public Bookmark? Since { get; init; }
        public IReadOnlyCollection<string>? Kinds { get; init; }
    }

    public sealed record CompleteResult
    {
        /// <summary>
        /// ok | paused
        /// </summary>
        public required string Status { get; init; }
        public required string Text { get; init; }
        public Bookmark? Last { get; init; }
        public required IReadOnlyList<string> PermissionIds { get; init; }
    }

    /// <summary>
    /// TS-aligned: <c>agent.chatStream(input, { since?, kinds? })</c>.
    /// Streams progress envelopes until a <c>done</c> event is observed.
    /// </summary>
    public IAsyncEnumerable<EventEnvelope<ProgressEvent>> ChatStream(
        string input,
        StreamOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        return ChatStreamAsync(input, opts, cancellationToken);
    }

    /// <summary>
    /// C# alias for <see cref="ChatStream"/> (kept for existing naming conventions).
    /// </summary>
    public async IAsyncEnumerable<EventEnvelope<ProgressEvent>> ChatStreamAsync(
        string input,
        StreamOptions? opts = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var since = opts?.Since ?? _eventBus.GetLastBookmark();
        Send(input, new SendOptions { Kind = PendingKind.User });

        await foreach (var envelope in _eventBus.SubscribeProgressAsync(since, opts?.Kinds, cancellationToken))
        {
            yield return envelope;
            if (envelope.Event is DoneEvent)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// TS-aligned: <c>agent.chat(input, { since?, kinds? })</c>.
    /// </summary>
    public async Task<CompleteResult> ChatAsync(
        string input,
        StreamOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var streamedText = new System.Text.StringBuilder();
        Bookmark? last = null;

        await foreach (var envelope in ChatStreamAsync(input, opts, cancellationToken))
        {
            if (envelope.Event is TextChunkEvent textChunk)
            {
                if (!string.IsNullOrEmpty(textChunk.Delta))
                {
                    streamedText.Append(textChunk.Delta);
                }
            }
            else if (envelope.Event is DoneEvent)
            {
                last = envelope.Bookmark;
            }
        }

        var pending = _permissionManager.GetPendingApprovalIds();

        var finalText = streamedText.ToString();
        var lastAssistant = _messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
        if (lastAssistant != null)
        {
            var combined = string.Join(
                "\n",
                lastAssistant.Content.OfType<TextContent>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
            if (combined.Trim().Length > 0)
            {
                finalText = combined;
            }
        }

        return new CompleteResult
        {
            Status = pending.Count > 0 ? "paused" : "ok",
            Text = finalText,
            Last = last,
            PermissionIds = pending
        };
    }

    /// <summary>
    /// TS-aligned alias: <c>agent.complete(...)</c>.
    /// </summary>
    public Task<CompleteResult> CompleteAsync(
        string input,
        StreamOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        return ChatAsync(input, opts, cancellationToken);
    }

    /// <summary>
    /// TS-aligned alias: <c>agent.stream(...)</c>.
    /// </summary>
    public IAsyncEnumerable<EventEnvelope<ProgressEvent>> Stream(
        string input,
        StreamOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        return ChatStreamAsync(input, opts, cancellationToken);
    }

    /// <summary>
    /// TS-aligned: <c>await agent.send(...)</c> returns messageId.
    /// </summary>
    public Task<string> SendAsync(string text, SendOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Send(text, options));
    }

    /// <inheritdoc />
    public async Task<AgentStepResult> StepAsync(CancellationToken cancellationToken = default)
    {
        using var stepActivity = KodeAgentActivitySource.Source.StartActivity("agent.step");
        var step = _stepCount;
        stepActivity?.SetTag("step.number", step);
        var stepStartMs = NowMs();
        TouchProcessingHeartbeat();

        // TS-aligned: if interrupted, stop the step early (run loop will return to READY).
        if (Interlocked.Exchange(ref _interrupted, 0) == 1)
        {
            return new AgentStepResult
            {
                StepType = StepType.ModelCall,
                HasMoreSteps = false
            };
        }

        // Flush queued user/reminder messages before any model call (aligned with TS runStep).
        await _messageQueue.FlushAsync(cancellationToken);

        // Hard stop: MaxIterations (best-effort guard; TS has no direct equivalent).
        if (_iterationCount >= _config.MaxIterations)
        {
            var envelope = _eventBus.EmitProgress(new DoneEvent
            {
                Type = "done",
                Step = step,
                Reason = _permissionManager.GetPendingApprovalIds().Count > 0 ? "interrupted" : "completed"
            });

            _stepCount++;
            _scheduler.NotifyStep(_stepCount);
            _todoManager?.OnStep(cancellationToken);
            var stepDurationMs = Math.Max(0, NowMs() - stepStartMs);
            _eventBus.EmitMonitor(new StepCompleteEvent
            {
                Type = "step_complete",
                Step = _stepCount,
                DurationMs = stepDurationMs
            });
            KodeAgentMetrics.StepsCompleted.Add(1);
            KodeAgentMetrics.StepDuration.Record(stepDurationMs);
            _iterationCount++;

            return new AgentStepResult
            {
                StepType = StepType.ModelCall,
                HasMoreSteps = false
            };
        }

        // Defensive recovery: remove dangling user turns from a previous failed run, then seal
        // orphan tool calls.  Order matters: user-turn cleanup must come first so the orphan
        // sanitizers don't act on messages that are about to be dropped.
        var danglingRemoved = SanitizeDanglingUserTurns();
        if (danglingRemoved > 0)
        {
            _eventBus.EmitMonitor(new AgentRecoveredEvent
            {
                Type = "agent_recovered",
                Reason = "dangling_user_turn",
                Detail = new { removed = danglingRemoved }
            });
            await _hookManager.RunMessagesChangedAsync(_messages, cancellationToken);
            await SaveStateAsync(cancellationToken);
        }

        await AutoSealDanglingToolUsesAsync("Sealed missing tool_result before model call.", cancellationToken);
        if (await SanitizeOrphanToolResultsAsync(cancellationToken) > 0)
        {
            await _hookManager.RunMessagesChangedAsync(_messages, cancellationToken);
        }

        // Context compression (aligned with TS: compress before model call).
        // System prompt tokens are added so compression triggers before the prompt itself
        // crowds out all headroom from the MaxTokens budget.
        var systemPromptTokens = ContextManager.EstimateSystemPromptTokens(_systemPrompt);
        var usage = _contextManager.Analyze(_messages, systemPromptTokens);
        if (usage.ShouldCompress)
        {
            _eventBus.EmitMonitor(new ContextCompressionEvent
            {
                Type = "context_compression",
                Phase = "start"
            });

            // OT-1D: span around context compression so its latency is visible in traces.
            using var compressActivity = KodeAgentActivitySource.Source.StartActivity("agent.context_compress");
            compressActivity?.SetTag("messages.count_before", _messages.Count);

            var compression = await _contextManager.CompressAsync(
                _messages,
                _eventBus.GetTimelineSnapshot(),
                _filePool,
                _sandbox,
                systemPromptTokens,
                cancellationToken: cancellationToken);

            if (compression != null)
            {
                // RetainedMessages is the fully-reconstructed list:
                // [core-memory?] + [summary stack] + [recent messages]
                _messages.Clear();
                _messages.AddRange(compression.RetainedMessages);
                await _hookManager.RunMessagesChangedAsync(_messages, cancellationToken);
                await SaveStateAsync(cancellationToken);

                compressActivity?.SetTag("compression.ratio", compression.Ratio);
                compressActivity?.SetTag("messages.count_after", _messages.Count);

                _eventBus.EmitMonitor(new ContextCompressionEvent
                {
                    Type = "context_compression",
                    Phase = "end",
                    Summary = string.Join("\n", compression.Summary.Content.OfType<TextContent>().Select(t => t.Text)),
                    Ratio = compression.Ratio
                });
                KodeAgentMetrics.ContextCompressions.Add(1);
            }
        }

        // Compression (or prior corruption) may introduce orphan tool_result or dangling tool_use; re-run defensively.
        if (await SanitizeOrphanToolResultsAsync(cancellationToken) > 0)
        {
            await _hookManager.RunMessagesChangedAsync(_messages, cancellationToken);
        }
        await AutoSealDanglingToolUsesAsync("Sealed missing tool_result after context compression.", cancellationToken);

        // Step 1: Call the model
        _breakpointManager.TransitionTo(BreakpointState.PreModel);

        var request = BuildModelRequest();
        await _hookManager.RunPreModelAsync(request, cancellationToken);
        _breakpointManager.TransitionTo(BreakpointState.StreamingModel);

        var response = await StreamModelResponseAsync(request, cancellationToken);
        await _hookManager.RunPostModelAsync(response, cancellationToken);

        // Empty content with no tools/text/thinking.
        // Some models (e.g. GLM-5-Turbo) return empty on context overflow rather than an error code.
        // Attempt one forced compression + retry before giving up.
        if (response.Content.Count == 0)
        {
            var forced = await _contextManager.CompressAsync(
                _messages, _eventBus.GetTimelineSnapshot(),
                _filePool, _sandbox, systemPromptTokens, force: true, cancellationToken);

            if (forced != null)
            {
                _messages.Clear();
                _messages.AddRange(forced.RetainedMessages);
                await _hookManager.RunMessagesChangedAsync(_messages, cancellationToken);
                await SaveStateAsync(cancellationToken);

                _eventBus.EmitMonitor(new ContextCompressionEvent
                {
                    Type = "context_compression",
                    Phase = "end",
                    Summary = string.Join("\n", forced.Summary.Content.OfType<TextContent>().Select(t => t.Text)),
                    Ratio = forced.Ratio
                });
                KodeAgentMetrics.ContextCompressions.Add(1);

                // Re-sanitize after force-compress (same as normal compress path).
                if (await SanitizeOrphanToolResultsAsync(cancellationToken) > 0)
                    await _hookManager.RunMessagesChangedAsync(_messages, cancellationToken);
                await AutoSealDanglingToolUsesAsync("Sealed after force-compress retry.", cancellationToken);

                // Rebuild request with compressed _messages; _nextModelToolsOverride already null from
                // first BuildModelRequest() call above, so this safely uses the normal tool list.
                var retryRequest = BuildModelRequest();
                await _hookManager.RunPreModelAsync(retryRequest, cancellationToken);
                _breakpointManager.TransitionTo(BreakpointState.StreamingModel);
                response = await StreamModelResponseAsync(retryRequest, cancellationToken);
                await _hookManager.RunPostModelAsync(response, cancellationToken);
            }

            if (response.Content.Count == 0)
                throw new InvalidOperationException("model_empty_response");
        }

        // Add assistant message
        _messages.Add(Message.Assistant(response.Content.ToArray()));
        await _hookManager.RunMessagesChangedAsync(_messages, cancellationToken);
        await SaveStateAsync(cancellationToken);

        // Check if we have tool calls
        var toolUses = response.Content.OfType<ToolUseContent>().ToList();
        AgentStepResult stepResult;
        Bookmark? doneBookmark = null;
        var shouldPersistReadyState = false;

        if (toolUses.Count > 0)
        {
            _breakpointManager.TransitionTo(BreakpointState.ToolPending);

            // Check permissions and execute tools
            var toolResults = await ProcessToolCallsAsync(toolUses, cancellationToken);

            // Add tool results as user message
            var resultContents = toolResults.Select(r => new ToolResultContent
            {
                ToolUseId = r.CallId,
                Content = r.Result.Success ? r.Result.Value ?? "Success" : r.Result.Error ?? "Error",
                IsError = !r.Result.Success
            }).Cast<ContentBlock>().ToList();

            // Loop detection: count (tool:input_hash) occurrences; nudge toward summary at 3 repeats.
            foreach (var toolUse in toolUses)
            {
                var fp = $"{toolUse.Name}:{System.Text.Json.JsonSerializer.Serialize(toolUse.Input).GetHashCode()}";
                _toolCallFingerprints.TryGetValue(fp, out var prev);
                var next = prev + 1;
                _toolCallFingerprints[fp] = next;
                if (next >= 3 && string.IsNullOrWhiteSpace(_nextModelNudgeText))
                {
                    _nextModelNudgeText =
                        $"You have called `{toolUse.Name}` with the same arguments {next} times. " +
                        "You may be stuck in a loop. Stop calling tools and write your final summary now.";
                }
            }

            if (!string.IsNullOrWhiteSpace(_nextModelNudgeText))
            {
                resultContents.Insert(0, new TextContent { Text = _nextModelNudgeText });
                _nextModelNudgeText = null;
            }

            _messages.Add(new Message
            {
                Role = MessageRole.User,
                Content = resultContents
            });
            await _hookManager.RunMessagesChangedAsync(_messages, cancellationToken);
            await SaveStateAsync(cancellationToken);

            _breakpointManager.TransitionTo(BreakpointState.PostTool);
            stepResult = new AgentStepResult
            {
                StepType = StepType.ToolExecution,
                HasMoreSteps = true,
                ToolCalls = toolUses.Select(tu => new ToolCallInfo
                {
                    CallId = tu.Id,
                    ToolName = tu.Name,
                    Arguments = tu.Input,
                    State = toolResults.FirstOrDefault(r => r.CallId == tu.Id).Result.Success
                        ? ToolCallState.Completed
                        : ToolCallState.Failed
                }).ToList()
            };
        }
        else
        {
            // No tool calls, we're done
            _breakpointManager.TransitionTo(BreakpointState.Ready);
            shouldPersistReadyState = true;

            var envelope = _eventBus.EmitProgress(new DoneEvent
            {
                Type = "done",
                Step = step,
                Reason = _permissionManager.GetPendingApprovalIds().Count > 0 ? "interrupted" : "completed"
            });
            doneBookmark = envelope.Bookmark;

            stepResult = new AgentStepResult
            {
                StepType = StepType.ModelCall,
                HasMoreSteps = response.StopReason == ModelStopReason.ToolUse
            };
        }

        _stepCount++;
        if (doneBookmark != null)
        {
            _scheduler.NotifyStep(_stepCount);
        }
        _todoManager?.OnStep(cancellationToken);
        var stepDuration = Math.Max(0, NowMs() - stepStartMs);
        KodeAgentMetrics.StepsCompleted.Add(1);
        KodeAgentMetrics.StepDuration.Record(stepDuration);
        if (doneBookmark != null)
        {
            _eventBus.EmitMonitor(new StepCompleteEvent
            {
                Type = "step_complete",
                Step = _stepCount,
                DurationMs = stepDuration
            });
        }
        _iterationCount++;

        if (shouldPersistReadyState)
        {
            await SaveStateAsync(cancellationToken);
        }

        return stepResult;
    }

    /// <inheritdoc />
    public Task PauseAsync()
    {
        TransitionState(AgentRuntimeState.Paused);
        _runCts?.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (_runtimeState != AgentRuntimeState.Paused)
        {
            throw new InvalidAgentStateException(_runtimeState, AgentRuntimeState.Paused);
        }

        TransitionState(AgentRuntimeState.Working);

        // Continue from where we left off
        while (_runtimeState == AgentRuntimeState.Working)
        {
            var step = await StepAsync(cancellationToken);
            if (!step.HasMoreSteps) break;
        }

        TransitionState(AgentRuntimeState.Ready);
    }

    /// <inheritdoc />
    public Task ApproveToolCallAsync(string callId)
    {
        _permissionManager.Approve(callId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DenyToolCallAsync(string callId, string? reason = null)
    {
        _permissionManager.Deny(callId, reason);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<string> SnapshotAsync(string? label = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var lastSfpIndex = FindLastSfpIndex();
        var snapshotId = string.IsNullOrWhiteSpace(label) ? $"sfp:{lastSfpIndex}" : label.Trim();
        var lastBookmark = _eventBus.LastBookmark ?? new Bookmark
        {
            Seq = -1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var clonedMessages = JsonSerializer.Deserialize<List<Message>>(
                                 JsonSerializer.Serialize(_messages, MetaJsonOptions),
                                 MetaJsonOptions)
                             ?? _messages.ToList();

        var metadata = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["stepCount"] = JsonSerializer.SerializeToElement(_stepCount, MetaJsonOptions)
        };

        var snapshot = new Snapshot
        {
            Id = snapshotId,
            Messages = clonedMessages,
            LastSfpIndex = lastSfpIndex,
            LastBookmark = lastBookmark,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Metadata = metadata
        };

        await _dependencies.Store.SaveSnapshotAsync(AgentId, snapshot, cancellationToken);

        return snapshotId;
    }

    /// <inheritdoc />
    public async Task<IAgent> ForkAsync(string newAgentId, CancellationToken cancellationToken = default, string? snapshotId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // TS-aligned: fork from a snapshot; when omitted, create one first.
        snapshotId ??= await SnapshotAsync(label: null, cancellationToken);

        var snapshot = await _dependencies.Store.LoadSnapshotAsync(AgentId, snapshotId, cancellationToken);
        if (snapshot == null)
        {
            throw new InvalidOperationException($"Snapshot not found: {snapshotId}");
        }

        // Create new agent with the same config.
        var forkedAgent = new Agent(newAgentId, _config, _dependencies);
        forkedAgent._sandbox = await _dependencies.SandboxFactory.CreateAsync(_config.SandboxOptions, cancellationToken);
        forkedAgent.InitializeToolServices();

        // Copy message history + derive stepCount (TS: snapshot.metadata.stepCount, else count user turns).
        forkedAgent._messages.AddRange(snapshot.Messages);
        forkedAgent._stepCount = TryReadInt(snapshot.Metadata, "stepCount") ?? forkedAgent._messages.Count(m => m.Role == MessageRole.User);
        forkedAgent._lineage = [.._lineage, AgentId];

        // Persist forked messages to storage (TS: persistMessages()).
        await _dependencies.Store.SaveMessagesAsync(newAgentId, forkedAgent._messages, cancellationToken);

        _logger?.LogInformation("Forked agent {SourceId} to {TargetId}", AgentId, newAgentId);
        return forkedAgent;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TodoItem>> GetTodosAsync(CancellationToken cancellationToken = default)
    {
        if (_todoManager is { Enabled: true })
        {
            return _todoManager.List();
        }

        var snapshot = await _dependencies.Store.LoadTodosAsync(AgentId, cancellationToken);
        return snapshot?.Todos ?? [];
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HistoryWindow>> GetHistoryWindowsAsync(CancellationToken cancellationToken = default)
        => _contextManager.LoadHistoryAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> ForceCompressAsync(CancellationToken cancellationToken = default)
    {
        var systemPromptTokens = ContextManager.EstimateSystemPromptTokens(_systemPrompt);
        var result = await _contextManager.CompressAsync(
            _messages,
            _eventBus.GetTimelineSnapshot(),
            _filePool,
            _sandbox,
            systemPromptTokens,
            force: true,
            cancellationToken);

        if (result is null)
            return false;

        _messages.Clear();
        _messages.AddRange(result.RetainedMessages);
        await _hookManager.RunMessagesChangedAsync(_messages, cancellationToken);
        await SaveStateAsync(cancellationToken);

        _eventBus.EmitMonitor(new ContextCompressionEvent
        {
            Type = "context_compression",
            Phase = "end",
            Summary = string.Join("\n", result.Summary.Content.OfType<TextContent>().Select(t => t.Text)),
            Ratio = result.Ratio
        });

        return true;
    }

    /// <inheritdoc />
    public async Task SetTodosAsync(IEnumerable<TodoItem> todos, CancellationToken cancellationToken = default)
    {
        var todoList = todos.ToList();

        // Validate: only one in_progress
        var inProgressCount = todoList.Count(t => t.Status == TodoStatus.InProgress);
        if (inProgressCount > 1)
        {
            throw new InvalidOperationException(
                $"Only one todo can be 'InProgress' at a time. Found {inProgressCount}.");
        }

        if (_todoManager is { Enabled: true })
        {
            await _todoManager.SetTodosAsync(todoList, cancellationToken);
            return;
        }

        var snapshot = new TodoSnapshot
        {
            Todos = todoList,
            Version = 1,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        await _dependencies.Store.SaveTodosAsync(AgentId, snapshot, cancellationToken);

        _logger?.LogDebug("Updated {Count} todos for agent {AgentId}", todoList.Count, AgentId);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        lock (_processingLock)
        {
            _processingCts?.Cancel();
            _processingCts?.Dispose();
            _processingCts = null;
        }
        lock (_activeToolCallsLock)
        {
            foreach (var cts in _activeToolCalls.Values)
            {
                try
                {
                    cts.Cancel();
                }
                catch
                {
                    // ignore
                }
                cts.Dispose();
            }
            _activeToolCalls.Clear();
        }
        _scheduler.Dispose();
        _messageQueue.Complete();

        await _eventBus.DisposeAsync();
        await _toolRunner.DisposeAsync();

        _filePool?.Dispose();

        if (_sandbox != null)
        {
            await _sandbox.DisposeAsync();
        }
    }

    private void LoadTools()
    {
        _tools.Clear();

        // TS-aligned resume: restore tool instances from persisted tool descriptors (name/registryId + config).
        if (_persistedToolDescriptors is { Count: > 0 })
        {
            foreach (var descriptor in _persistedToolDescriptors)
            {
                var id = !string.IsNullOrWhiteSpace(descriptor.RegistryId) ? descriptor.RegistryId : descriptor.Name;
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidOperationException("Corrupted tool descriptor: missing name/registryId");
                }

                if (!_dependencies.ToolRegistry.Has(id))
                {
                    throw new InvalidOperationException($"Failed to restore tool '{descriptor.Name}': not registered (id='{id}')");
                }

                _tools.Add(_dependencies.ToolRegistry.Create(id, descriptor.Config));
            }

            return;
        }

        if (_config.Tools == null || _config.Tools.Count == 0)
        {
            return;
        }

        var allowAll = _config.Tools.Any(t => string.Equals(t, "*", StringComparison.Ordinal));
        if (allowAll)
        {
            foreach (var toolName in _dependencies.ToolRegistry.List())
            {
                if (_dependencies.ToolRegistry.Has(toolName))
                {
                    _tools.Add(_dependencies.ToolRegistry.Create(toolName));
                }
            }
            return;
        }

        foreach (var toolName in _config.Tools)
        {
            if (_dependencies.ToolRegistry.Has(toolName))
            {
                var tool = _dependencies.ToolRegistry.Create(toolName);
                _tools.Add(tool);
            }
        }
    }

    private ModelRequest BuildModelRequest()
    {
        var toolsToExpose = _tools
            .Where(t => _permissionManager.IsSchemaVisible(t.Name))
            .AsEnumerable();
        var toolsOverride = _nextModelToolsOverride;
        if (toolsOverride != null)
        {
            _nextModelToolsOverride = null;
            if (toolsOverride.Mode == NextModelToolsMode.None)
            {
                toolsToExpose = [];
            }
            else if (toolsOverride.Mode == NextModelToolsMode.Allowlist && toolsOverride.Allow is { Count: > 0 })
            {
                var allow = new HashSet<string>(toolsOverride.Allow, StringComparer.OrdinalIgnoreCase);
                toolsToExpose = toolsToExpose.Where(t => allow.Contains(t.Name));
            }
        }

        var toolSchemas = toolsToExpose.Select(t => new ToolSchema
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = t.InputSchema
        }).ToList();

        return new ModelRequest
        {
            Model = _config.Model!,
            Messages = _messages.ToList(),
            SystemPrompt = _systemPrompt,
            Tools = toolSchemas,
            MaxTokens = _config.MaxTokens,
            Temperature = _config.Temperature,
            EnableThinking = _currentRunOptions?.EnableThinking ?? _config.EnableThinking,
            ThinkingBudget = _currentRunOptions?.ThinkingBudget ?? _config.ThinkingBudget
        };
    }

    private async Task<ModelResponse> StreamModelResponseAsync(
        ModelRequest request,
        CancellationToken cancellationToken)
    {
        using var modelActivity = KodeAgentActivitySource.Source.StartActivity("agent.model_request");
        modelActivity?.SetTag("gen_ai.request.model", request.Model);
        modelActivity?.SetTag("has_tools", request.Tools?.Count > 0);
        modelActivity?.SetTag("agent.session_type", _config.SessionType);
        modelActivity?.SetTag("agent.role", _config.AgentRole);
        // OT-2A: include session_type and agent_role for token cost attribution.
        var modelDimensions = new TagList
        {
            { "model", request.Model },
            { "session_type", _config.SessionType },
            { "agent_role", _config.AgentRole }
        };
        KodeAgentMetrics.ModelRequests.Add(1, modelDimensions);
        var modelStopwatch = Stopwatch.StartNew();

        var step = _stepCount;
        TouchProcessingHeartbeat();
        var contentBlocks = new List<ContentBlock>();
        var textBuilder = new System.Text.StringBuilder();
        var thinkingBuilder = new System.Text.StringBuilder();
        var toolUseBuilders = new Dictionary<string, (string Name, System.Text.StringBuilder Input)>();
        TokenUsage? usage = null;
        ModelStopReason stopReason = ModelStopReason.EndTurn;
        var textStarted = false;
        var thinkingStarted = false;

        // Install SDK-level retry hook so ProviderRetryHelper emits ModelRetryingEvent on every
        // back-off delay — all consumers (chat, channel, automation) benefit automatically.
        AgentRetryContext.Current.Value = ctx =>
        {
            try
            {
                _eventBus.EmitMonitor(new ModelRetryingEvent
                {
                    Type = "model:retrying",
                    Provider = ctx.ProviderName,
                    Attempt = ctx.Attempt,
                    MaxRetries = ctx.MaxRetries,
                    DelaySeconds = Math.Round(ctx.Delay.TotalSeconds, 1),
                    Reason = ctx.ErrorMessage,
                });
            }
            catch { /* never let event emission crash the provider call */ }
        };
        try
        {

        // Retry loop: up to 3 attempts for transient provider errors (e.g. 500 / 503).
        // We only retry when no content has been emitted to the event bus yet — otherwise
        // partial output would be duplicated on the client side.
        const int maxProviderAttempts = 3;
        for (var providerAttempt = 1; providerAttempt <= maxProviderAttempts; providerAttempt++)
        {
            var attemptSucceeded = false;
            try
            {
                await foreach (var chunk in _dependencies.ModelProvider.StreamAsync(request, cancellationToken))
                {
                    // Heartbeat: any streamed chunk indicates forward progress (aligned with TS lastProcessingStart updates).
                    TouchProcessingHeartbeat();
                    switch (chunk.Type)
                    {
                        case StreamChunkType.TextDelta:
                            if (chunk.TextDelta != null)
                            {
                                if (!textStarted)
                                {
                                    textStarted = true;
                                    _eventBus.EmitProgress(new TextChunkStartEvent
                                    {
                                        Type = "text_chunk_start",
                                        Step = step
                                    });
                                }
                                textBuilder.Append(chunk.TextDelta);
                                _eventBus.EmitProgress(new TextChunkEvent
                                {
                                    Type = "text_chunk",
                                    Step = step,
                                    Delta = chunk.TextDelta
                                });
                            }
                            break;

                        case StreamChunkType.ThinkingDelta:
                            if (chunk.ThinkingDelta != null)
                            {
                                if (_config.ExposeThinking == true)
                                {
                                    if (!thinkingStarted)
                                    {
                                        thinkingStarted = true;
                                        _eventBus.EmitProgress(new ThinkChunkStartEvent
                                        {
                                            Type = "think_chunk_start",
                                            Step = step
                                        });
                                    }
                                    thinkingBuilder.Append(chunk.ThinkingDelta);
                                    _eventBus.EmitProgress(new ThinkChunkEvent
                                    {
                                        Type = "think_chunk",
                                        Step = step,
                                        Delta = chunk.ThinkingDelta
                                    });
                                }
                            }
                            break;

                        case StreamChunkType.ToolUseStart:
                            if (chunk.ToolUse != null)
                            {
                                toolUseBuilders[chunk.ToolUse.Id] = (chunk.ToolUse.Name!, new System.Text.StringBuilder());
                            }
                            break;

                        case StreamChunkType.ToolUseInputDelta:
                            if (chunk.ToolUse != null && toolUseBuilders.TryGetValue(chunk.ToolUse.Id, out var builder))
                            {
                                builder.Input.Append(chunk.ToolUse.InputDelta);
                            }
                            break;

                        case StreamChunkType.ToolUseComplete:
                            if (chunk.ToolUse != null && toolUseBuilders.TryGetValue(chunk.ToolUse.Id, out var completedBuilder))
                            {
                                var inputJson = completedBuilder.Input.ToString();
                                var input = string.IsNullOrEmpty(inputJson)
                                    ? new { }
                                    : System.Text.Json.JsonSerializer.Deserialize<object>(inputJson) ?? new { };

                                contentBlocks.Add(new ToolUseContent
                                {
                                    Id = chunk.ToolUse.Id,
                                    Name = completedBuilder.Name,
                                    Input = input
                                });
                            }
                            break;

                        case StreamChunkType.MessageStop:
                            if (chunk.Usage != null) usage = chunk.Usage;
                            stopReason = chunk.StopReason ?? ModelStopReason.EndTurn;
                            break;
                    }
                }
                attemptSucceeded = true;
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested
                && ex is not OperationCanceledException
                && ex is not TaskCanceledException
                // Only retry when nothing has been emitted to the event bus — otherwise
                // partial content would be duplicated on the consumer side.
                && !textStarted
                && !thinkingStarted
                && contentBlocks.Count == 0
                && toolUseBuilders.Count == 0
                && providerAttempt < maxProviderAttempts)
            {
                // OT-2C: classify error type for metric tagging.
                var errorType = ClassifyModelError(ex);
                KodeAgentMetrics.ModelErrors.Add(1, new TagList
                {
                    { "model", request.Model },
                    { "error_type", errorType }
                });
                modelActivity?.SetTag("agent.retry_count", providerAttempt);

                // Transient provider error before any content was produced — retry with backoff.
                _eventBus.EmitMonitor(new ErrorEvent
                {
                    Type = "provider_retry",
                    Severity = "warn",
                    Phase = "model",
                    Message = $"Provider error on attempt {providerAttempt}/{maxProviderAttempts}, retrying. {ex.GetBaseException().Message}"
                });
                var delayMs = providerAttempt == 1 ? 1000 : 2000;
                await Task.Delay(delayMs, cancellationToken);
            }

            if (attemptSucceeded)
            {
                break;
            }
        }

        } // end try (AgentRetryContext)
        finally
        {
            AgentRetryContext.Current.Value = null;
        }

        // Add text content if any
        if (textBuilder.Length > 0)
        {
            contentBlocks.Insert(0, new TextContent { Text = textBuilder.ToString() });
        }

        if (textStarted)
        {
            _eventBus.EmitProgress(new TextChunkEndEvent
            {
                Type = "text_chunk_end",
                Step = step,
                Text = textBuilder.ToString()
            });
        }

        // Add thinking content if any (only if exposeThinking enabled)
        if (_config.ExposeThinking == true && thinkingBuilder.Length > 0)
        {
            contentBlocks.Insert(0, new ThinkingContent { Thinking = thinkingBuilder.ToString() });
        }

        if (thinkingStarted)
        {
            _eventBus.EmitProgress(new ThinkChunkEndEvent
            {
                Type = "think_chunk_end",
                Step = step
            });
        }

        if (usage != null)
        {
            _eventBus.EmitMonitor(new TokenUsageEvent
            {
                Type = "token_usage",
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                TotalTokens = usage.InputTokens + usage.OutputTokens
            });
            KodeAgentMetrics.TokensInput.Add(usage.InputTokens, modelDimensions);
            KodeAgentMetrics.TokensOutput.Add(usage.OutputTokens, modelDimensions);
            modelActivity?.SetTag("gen_ai.usage.input_tokens", usage.InputTokens);
            modelActivity?.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
        }

        modelStopwatch.Stop();
        KodeAgentMetrics.ModelRequestDuration.Record(modelStopwatch.Elapsed.TotalMilliseconds, modelDimensions);

        return new ModelResponse
        {
            Content = contentBlocks,
            StopReason = stopReason,
            Usage = usage ?? new TokenUsage { InputTokens = 0, OutputTokens = 0 },
            Model = _config.Model!
        };
    }

    private async Task<List<(string CallId, ToolResult Result)>> ProcessToolCallsAsync(
        List<ToolUseContent> toolUses,
        CancellationToken cancellationToken)
    {
        var results = new List<(string CallId, ToolResult Result)>();

        // Compute context pressure once per tool batch so each tool can adapt its output size.
        var contextPressure = ComputeContextPressure();

        foreach (var toolUse in toolUses)
        {
            _toolRunner.RegisterToolCall(toolUse.Id, toolUse.Name, toolUse.Input);
            var startSnap = _toolRunner.GetSnapshot(toolUse.Id) ?? new ToolCallSnapshot
            {
                Id = toolUse.Id,
                Name = toolUse.Name,
                State = ToolCallState.Pending,
                Approval = new ToolCallApproval { Required = false }
            };
            _eventBus.EmitProgress(new ToolStartEvent
            {
                Type = "tool:start",
                Call = startSnap
            });

            var hookCall = new ToolCall(
                toolUse.Id,
                toolUse.Name,
                System.Text.Json.JsonSerializer.SerializeToElement(toolUse.Input));

            var context = new ToolContext
            {
                AgentId = AgentId,
                CallId = toolUse.Id,
                Sandbox = _sandbox!,
                SandboxOptions = _config.SandboxOptions,
                Agent = this,
                Services = _toolServices,
                ContextPressure = contextPressure,
                Emit = (eventType, data) =>
                {
                    _eventBus.EmitMonitor(new ToolCustomEvent
                    {
                        Type = "tool_custom_event",
                        ToolName = toolUse.Name,
                        EventType = eventType,
                        Data = data,
                        Timestamp = NowMs()
                    });
                },
                CancellationToken = cancellationToken
            };

            var preDecision = await _hookManager.RunPreToolUseAsync(hookCall, context, cancellationToken);
            if (preDecision is DenyDecision deny)
            {
                _toolRunner.DenyToolCall(toolUse.Id, deny.Reason);
                _eventBus.EmitProgress(new ToolEndEvent { Type = "tool:end", Call = GetSnapshotOrFallback(toolUse.Id, toolUse.Name) });
                results.Add((toolUse.Id, ToolResult.Fail(deny.Reason)));
                continue;
            }

            if (preDecision is SkipDecision skip)
            {
                var mock = ToolResult.Ok(skip.MockResult);
                _toolRunner.UpdateFinalResult(toolUse.Id, mock);
                var mockSnapshot = _toolRunner.GetSnapshot(toolUse.Id);
                if (mockSnapshot != null && mock.Success)
                {
                    _eventBus.EmitMonitor(new ToolExecutedEvent
                    {
                        Type = "tool_executed",
                        Call = mockSnapshot
                    });
                }
                _eventBus.EmitProgress(new ToolEndEvent
                {
                    Type = "tool:end",
                    Call = GetSnapshotOrFallback(toolUse.Id, toolUse.Name)
                });
                results.Add((toolUse.Id, mock));
                continue;
            }

            // Tool must be enabled/exposed for this agent run (align with TS tool selection semantics).
            if (_tools.All(t => !string.Equals(t.Name, toolUse.Name, StringComparison.OrdinalIgnoreCase)))
            {
                const string message = "Tool is not enabled for this agent";
                _toolRunner.DenyToolCall(toolUse.Id, message);
                _eventBus.EmitProgress(new ToolEndEvent { Type = "tool:end", Call = GetSnapshotOrFallback(toolUse.Id, toolUse.Name) });
                results.Add((toolUse.Id, ToolResult.Fail(message)));
                continue;
            }

            var toolInstance = _tools.FirstOrDefault(t => string.Equals(t.Name, toolUse.Name, StringComparison.OrdinalIgnoreCase));
            if (toolInstance != null)
            {
                // Tool input validation (best-effort; aligns with TS schema validation behavior).
                var validation = ToolInputValidator.Validate(toolInstance.InputSchema, toolUse.Input);
                if (!validation.Ok)
                {
                    if (string.Equals(_invalidToolArgsLastTool, toolUse.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        _invalidToolArgsStreak += 1;
                    }
                    else
                    {
                        _invalidToolArgsLastTool = toolUse.Name;
                        _invalidToolArgsStreak = 1;
                    }

                    if (_invalidToolArgsStreak >= 2)
                    {
                        _nextModelToolsOverride = new NextModelToolsOverride(
                            NextModelToolsMode.Allowlist,
                            [toolUse.Name],
                            "recover_invalid_tool_args");
                    }

                    if (_invalidToolArgsStreak >= 3)
                    {
                        var requiredKeys = validation.RequiredKeys is { Count: > 0 }
                            ? string.Join(", ", validation.RequiredKeys)
                            : "(see tool schema)";
                        _nextModelNudgeText =
                            $"Your last tool call to `{toolUse.Name}` failed schema validation ({validation.Error}). " +
                            $"Retry by emitting ONLY one `tool_use` for `{toolUse.Name}` with a complete JSON object. " +
                            $"Required keys: {requiredKeys}. Keep tool input small; if writing large files, write a short skeleton first and expand via multiple edits.";
                    }

                    if (_invalidToolArgsStreak >= 6)
                    {
                        _nextModelToolsOverride = new NextModelToolsOverride(
                            NextModelToolsMode.None,
                            null,
                            "invalid_tool_args_suppressed_auto_continue");
                        _nextModelNudgeText =
                            $"Tool calls are failing repeatedly (streak={_invalidToolArgsStreak}). " +
                            "In your next response, DO NOT call any tools. Explain the issue and propose a concrete next step (Retry, reduce output size, or split file writes).";
                    }

                    var message = $"Tool input validation failed for {toolUse.Name}: {validation.Error ?? "invalid input"}";
                    var fail = ToolResult.Fail(message);
                    _toolRunner.UpdateFinalResult(toolUse.Id, fail);
                    _eventBus.EmitProgress(new ToolErrorEvent
                    {
                        Type = "tool:error",
                        Call = GetSnapshotOrFallback(toolUse.Id, toolUse.Name),
                        Error = message
                    });
                    _eventBus.EmitProgress(new ToolEndEvent { Type = "tool:end", Call = GetSnapshotOrFallback(toolUse.Id, toolUse.Name) });
                    results.Add((toolUse.Id, fail));
                    continue;
                }

                // Any successful validation resets the streak.
                _invalidToolArgsStreak = 0;
                _invalidToolArgsLastTool = "";
            }

            // Hard deny: allowlist / deny list
            if (_permissionManager.IsDenied(toolUse.Name, out var denyReason))
            {
                _toolRunner.DenyToolCall(toolUse.Id, denyReason);
                _eventBus.EmitProgress(new ToolEndEvent { Type = "tool:end", Call = GetSnapshotOrFallback(toolUse.Id, toolUse.Name) });
                results.Add((toolUse.Id, ToolResult.Fail(denyReason)));
                continue;
            }

            // Check if approval is required
            var forceApproval = preDecision is RequireApprovalDecision;
            if (forceApproval || _permissionManager.RequiresApproval(toolUse.Name, toolUse.Input))
            {
                _breakpointManager.TransitionTo(BreakpointState.AwaitingApproval);
                TransitionState(AgentRuntimeState.Paused);

                // OT-1C: span measuring how long approval takes (human-in-the-loop wait time).
                bool approved;
                using (var approvalActivity = KodeAgentActivitySource.Source.StartActivity("agent.tool.approval_wait"))
                {
                    approvalActivity?.SetTag("tool.name", toolUse.Name);
                    approvalActivity?.SetTag("tool.id", toolUse.Id);
                    approved = await _permissionManager.RequestApprovalAsync(
                        toolUse.Id,
                        toolUse.Name,
                        toolUse.Input,
                        (preDecision as RequireApprovalDecision)?.Reason,
                        cancellationToken);
                    approvalActivity?.SetTag("tool.approved", approved);
                }

                // Always resume after a decision (approved or denied)
                TransitionState(AgentRuntimeState.Working);

                if (!approved)
                {
                    const string message = "Permission denied";
                    _toolRunner.DenyToolCall(toolUse.Id, message);
                    _eventBus.EmitProgress(new ToolEndEvent { Type = "tool:end", Call = GetSnapshotOrFallback(toolUse.Id, toolUse.Name) });
                    results.Add((toolUse.Id, ToolResult.Fail(message)));
                    continue;
                }
            }

            _breakpointManager.TransitionTo(BreakpointState.PreTool);

            _breakpointManager.TransitionTo(BreakpointState.ToolExecuting);

            using var toolActivity = KodeAgentActivitySource.Source.StartActivity("agent.tool.execute");
            toolActivity?.SetTag("tool.name", toolUse.Name);
            toolActivity?.SetTag("tool.id", toolUse.Id);

            TouchProcessingHeartbeat();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResult toolResult;
            try
            {
                var toolCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (_activeToolCallsLock)
                {
                    _activeToolCalls[toolUse.Id] = toolCts;
                }

                try
                {
                    if (_config.ToolTimeout > TimeSpan.Zero)
                    {
                        toolCts.CancelAfter(_config.ToolTimeout);
                    }

                    var execContext = context with { CancellationToken = toolCts.Token };

                toolResult = await _toolRunner.ExecuteAsync(
                        toolUse.Id, toolUse.Name, toolUse.Input, execContext, toolCts.Token);
                }
                finally
                {
                    lock (_activeToolCallsLock)
                    {
                        _activeToolCalls.Remove(toolUse.Id);
                    }
                    toolCts.Dispose();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                toolResult = ToolResult.Fail("Tool timed out or cancelled");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                toolResult = ToolResult.Fail(ex.Message);
            }
            sw.Stop();
            TouchProcessingHeartbeat();

            // OT-2E: tag tool metrics with category dimension for cost breakdown by tool type.
            var toolCategory = ClassifyToolCategory(toolUse.Name);
            var toolDimensions = new TagList
            {
                { "tool.name", toolUse.Name },
                { "tool_category", toolCategory }
            };
            KodeAgentMetrics.ToolExecutions.Add(1, toolDimensions);
            KodeAgentMetrics.ToolDuration.Record(sw.Elapsed.TotalMilliseconds, toolDimensions);
            toolActivity?.SetTag("tool.duration_ms", sw.Elapsed.TotalMilliseconds);
            toolActivity?.SetTag("tool.category", toolCategory);

            // OT-3B: desensitized structured log — no tool args/output, only metadata.
            _logger?.LogDebug(
                "Tool executed: {ToolName} category={ToolCategory} success={Success} duration_ms={DurationMs}",
                toolUse.Name, toolCategory, toolResult.Success, (long)sw.Elapsed.TotalMilliseconds);

            var outcome = new ToolOutcome(
                toolUse.Id,
                toolUse.Name,
                System.Text.Json.JsonSerializer.SerializeToElement(toolUse.Input),
                toolResult,
                !toolResult.Success,
                sw.Elapsed);

            var postOutcome = await _hookManager.RunPostToolUseAsync(outcome, context, cancellationToken);

            // Compress oversized tool results before they enter the message history.
            var finalResult = postOutcome.Result;
            if (_toolResultCompressor != null && _config.Context?.ToolResultCompression != null)
            {
                finalResult = await _toolResultCompressor.CompressIfNeededAsync(
                    toolUse.Name,
                    finalResult,
                    _messages,
                    _config.Context.ToolResultCompression,
                    cancellationToken);
            }

            _toolRunner.UpdateFinalResult(toolUse.Id, finalResult);
            var snapAfter = _toolRunner.GetSnapshot(toolUse.Id);
            if (snapAfter != null && finalResult.Success)
            {
                _eventBus.EmitMonitor(new ToolExecutedEvent
                {
                    Type = "tool_executed",
                    Call = snapAfter
                });
            }

            if (!finalResult.Success)
            {
                var message = finalResult.Error ?? "Tool failed";
                KodeAgentMetrics.ToolErrors.Add(1, toolDimensions);
                toolActivity?.SetStatus(ActivityStatusCode.Error, message);

                _eventBus.EmitProgress(new ToolErrorEvent
                {
                    Type = "tool:error",
                    Call = GetSnapshotOrFallback(toolUse.Id, toolUse.Name),
                    Error = message
                });

                _eventBus.EmitMonitor(new ErrorEvent
                {
                    Type = "error",
                    Severity = "warn",
                    Phase = "tool",
                    Message = message,
                    Detail = finalResult.Value
                });
            }

            _eventBus.EmitProgress(new ToolEndEvent { Type = "tool:end", Call = GetSnapshotOrFallback(toolUse.Id, toolUse.Name) });

            results.Add((toolUse.Id, finalResult));
        }

        return results;
    }

    /// <summary>
    /// OT-2C: classify an exception into a coarse error_type label for metrics.
    /// Labels: rate_limit | auth | server_error | timeout | unknown
    /// </summary>
    private static string ClassifyModelError(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        if (msg.Contains("429") || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return "rate_limit";
        if (msg.Contains("401") || msg.Contains("403") || msg.Contains("auth", StringComparison.OrdinalIgnoreCase))
            return "auth";
        if (msg.Contains("500") || msg.Contains("503") || msg.Contains("server error", StringComparison.OrdinalIgnoreCase))
            return "server_error";
        if (ex is TimeoutException
            || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "timeout";
        return "unknown";
    }

    /// <summary>
    /// OT-2E: classify a tool name into a coarse category for metrics dimension.
    /// Categories: filesystem | shell | web | orchestration | workspace | mcp | other
    /// </summary>
    private static string ClassifyToolCategory(string toolName)
    {
        if (toolName.StartsWith("fs_", StringComparison.OrdinalIgnoreCase)) return "filesystem";
        if (toolName.StartsWith("bash_", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("shell_", StringComparison.OrdinalIgnoreCase)) return "shell";
        if (toolName.StartsWith("web_", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("http_", StringComparison.OrdinalIgnoreCase)) return "web";
        if (toolName.StartsWith("isolate_task", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("pipeline", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("parallel_", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("retry_", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("fan_out", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("map_reduce", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("debate", StringComparison.OrdinalIgnoreCase)) return "orchestration";
        if (toolName.StartsWith("workspace_", StringComparison.OrdinalIgnoreCase)) return "workspace";
        if (toolName.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)) return "mcp";
        return "other";
    }

    /// <summary>
    /// Returns context pressure [0.0, ∞) — ratio of current token usage to the compression
    /// trigger threshold. 1.0 means exactly at threshold; >1.0 means over threshold.
    /// </summary>
    private float ComputeContextPressure()
    {
        var maxTokens = _config.Context?.MaxTokens ?? 0;
        if (maxTokens <= 0) return 0f;

        var systemPromptTokens = ContextManager.EstimateSystemPromptTokens(_systemPrompt);
        var usage = _contextManager.Analyze(_messages, systemPromptTokens);
        return (float)usage.TotalTokens / maxTokens;
    }

    private ToolCallSnapshot GetSnapshotOrFallback(string callId, string toolName)
    {
        return _toolRunner.GetSnapshot(callId) ?? new ToolCallSnapshot
        {
            Id = callId,
            Name = toolName,
            State = ToolCallState.Pending,
            Approval = new ToolCallApproval { Required = false }
        };
    }

    private static void RegisterHooks(HookManager hookManager, AgentConfig config, AgentDependencies dependencies)
    {
        if (dependencies.TemplateRegistry != null &&
            !string.IsNullOrWhiteSpace(config.TemplateId) &&
            dependencies.TemplateRegistry.TryGet(config.TemplateId!, out var tpl) &&
            tpl?.Hooks != null)
        {
            hookManager.Register(tpl.Hooks, HookOrigin.Agent);
        }

        if (config.Hooks != null)
        {
            foreach (var hooks in config.Hooks)
            {
                hookManager.Register(hooks, HookOrigin.Agent);
            }
        }
    }

    private static AgentConfig ApplyTemplateConfig(AgentConfig config, AgentDependencies dependencies)
    {
        if (dependencies.TemplateRegistry == null) return config;
        if (string.IsNullOrWhiteSpace(config.TemplateId)) return config;
        if (!dependencies.TemplateRegistry.TryGet(config.TemplateId, out var template) || template == null) return config;

        var merged = config;

        if (string.IsNullOrWhiteSpace(merged.SystemPrompt))
        {
            merged = merged with { SystemPrompt = template.SystemPrompt };
        }

        if (string.IsNullOrWhiteSpace(merged.Model) && !string.IsNullOrWhiteSpace(template.Model))
        {
            merged = merged with { Model = template.Model };
        }

        if (merged.Tools == null)
        {
            if (template.Tools.AllowAll)
            {
                merged = merged with { Tools = ["*"] };
            }
            else if (template.Tools.AllowedTools != null)
            {
                merged = merged with { Tools = template.Tools.AllowedTools.ToArray() };
            }
        }

        if (merged.Permissions == null && template.Permission != null)
        {
            merged = merged with
            {
                Permissions = new Kode.Agent.Sdk.Core.Types.PermissionConfig
                {
                    Mode = template.Permission.Mode,
                    AllowTools = template.Permission.AllowTools,
                    RequireApprovalTools = template.Permission.RequireApprovalTools,
                    DenyTools = template.Permission.DenyTools,
                    Metadata = template.Permission.Metadata?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase)
                }
            };
        }

        if (merged.SandboxOptions == null && template.Sandbox != null)
        {
            merged = merged with { SandboxOptions = ConvertSandboxOptions(template.Sandbox) };
        }

        if (template.Runtime?.Metadata != null)
        {
            if (template.Runtime.Metadata.TryGetValue("maxToolConcurrency", out var maxConc) &&
                maxConc.ValueKind == System.Text.Json.JsonValueKind.Number &&
                maxConc.TryGetInt32(out var value) &&
                value > 0 &&
                merged.MaxToolConcurrency == 3)
            {
                merged = merged with { MaxToolConcurrency = value };
            }

            if (template.Runtime.Metadata.TryGetValue("toolTimeoutMs", out var timeoutMs) &&
                timeoutMs.ValueKind == System.Text.Json.JsonValueKind.Number &&
                timeoutMs.TryGetInt32(out var ms) &&
                ms > 0 &&
                merged.ToolTimeout == TimeSpan.FromMinutes(10))
            {
                merged = merged with { ToolTimeout = TimeSpan.FromMilliseconds(ms) };
            }

            if (merged.Context == null &&
                template.Runtime.Metadata.TryGetValue("context", out var contextMeta) &&
                contextMeta.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                try
                {
                    var ctx = contextMeta.Deserialize<ContextManagerOptions>();
                    if (ctx != null)
                    {
                        merged = merged with { Context = ctx };
                    }
                }
                catch
                {
                    // ignore invalid metadata.context
                }
            }
        }

        if (merged.ExposeThinking is null && template.Runtime != null)
        {
            merged = merged with { ExposeThinking = template.Runtime.ExposeThinking };
        }

        if (merged.SubAgents == null && template.Runtime?.SubAgents != null)
        {
            merged = merged with { SubAgents = template.Runtime.SubAgents };
        }

        if (merged.Todo == null && template.Runtime?.Todo != null)
        {
            merged = merged with { Todo = template.Runtime.Todo };
        }

        return merged;
    }

    private static SandboxOptions ConvertSandboxOptions(IReadOnlyDictionary<string, System.Text.Json.JsonElement> sandbox)
    {
        var options = new SandboxOptions();

        if (sandbox.TryGetValue("workDir", out var workDir) && workDir.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            options = options with { WorkingDirectory = workDir.GetString() };
        }

        if (sandbox.TryGetValue("enforceBoundary", out var enforceBoundary) &&
            (enforceBoundary.ValueKind == System.Text.Json.JsonValueKind.True || enforceBoundary.ValueKind == System.Text.Json.JsonValueKind.False))
        {
            options = options with { EnforceBoundary = enforceBoundary.GetBoolean() };
        }

        if (sandbox.TryGetValue("allowPaths", out var allowPaths) && allowPaths.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var p in allowPaths.EnumerateArray())
            {
                if (p.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = p.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        list.Add(s);
                    }
                }
            }
            options = options with { AllowPaths = list };
        }

        if (sandbox.TryGetValue("watchFiles", out var watchFiles) &&
            (watchFiles.ValueKind == System.Text.Json.JsonValueKind.True || watchFiles.ValueKind == System.Text.Json.JsonValueKind.False))
        {
            options = options with { WatchFiles = watchFiles.GetBoolean() };
        }

        if (sandbox.TryGetValue("useDocker", out var useDocker) &&
            (useDocker.ValueKind == System.Text.Json.JsonValueKind.True || useDocker.ValueKind == System.Text.Json.JsonValueKind.False))
        {
            options = options with { UseDocker = useDocker.GetBoolean() };
        }

        if (sandbox.TryGetValue("dockerImage", out var dockerImage) && dockerImage.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            options = options with { DockerImage = dockerImage.GetString() };
        }

        if (sandbox.TryGetValue("dockerNetworkMode", out var dockerNetworkMode) && dockerNetworkMode.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            options = options with { DockerNetworkMode = dockerNetworkMode.GetString() };
        }

        if (sandbox.TryGetValue("sandboxStateDirectory", out var sandboxStateDirectory) && sandboxStateDirectory.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            options = options with { SandboxStateDirectory = sandboxStateDirectory.GetString() };
        }

        return options;
    }

    private enum NextModelToolsMode
    {
        None,
        Allowlist
    }

    private sealed record NextModelToolsOverride(
        NextModelToolsMode Mode,
        IReadOnlyList<string>? Allow,
        string Reason);

    private void TransitionState(AgentRuntimeState newState)
    {
        AgentRuntimeState previous;
        lock (_stateLock)
        {
            if (_runtimeState == newState) return;
            previous = _runtimeState;
            _runtimeState = newState;
        }

        // Track turn timing for /status command (lock-free via Interlocked)
        if (newState == AgentRuntimeState.Working && previous == AgentRuntimeState.Ready)
        {
            Interlocked.Exchange(ref _turnStartedAtMs, NowMs());
        }
        else if (newState == AgentRuntimeState.Ready)
        {
            Interlocked.Exchange(ref _lastActivityAtMs, NowMs());
            Interlocked.Exchange(ref _turnStartedAtMs, 0);
        }

        _eventBus.EmitMonitor(new StateChangedEvent
        {
            Type = "state_changed",
            State = newState
        });
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void TouchProcessingHeartbeat()
    {
        Interlocked.Exchange(ref _lastProcessingHeartbeatMs, NowMs());
    }

    private async Task SaveStateAsync()
    {
        try
        {
            await SaveStateAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save agent state");
        }
    }

    private async Task SaveStateAsync(CancellationToken cancellationToken)
    {
        await _dependencies.Store.SaveMessagesAsync(AgentId, _messages, cancellationToken);
        await _dependencies.Store.SaveToolCallRecordsAsync(AgentId, _toolRunner.ActiveToolCalls, cancellationToken);
        await UpdateInfoAsync(cancellationToken);
    }

    private async Task UpdateInfoAsync()
    {
        try
        {
            await UpdateInfoAsync(CancellationToken.None);
        }
        catch
        {
            // best-effort meta tracking; ignore failures
        }
    }

    private async Task UpdateInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _dependencies.Store.LoadInfoAsync(AgentId, cancellationToken);

            var info = (existing ?? new AgentInfo
            {
                AgentId = AgentId,
                CreatedAt = _createdAt,
                Lineage = []
            }) with
            {
                TemplateId = _config.TemplateId,
                ConfigVersion = typeof(Agent).Assembly.GetName().Version?.ToString(),
                MessageCount = _messages.Count,
                LastSfpIndex = FindLastSfpIndex(),
                LastBookmark = _eventBus.LastBookmark,
                Breakpoint = _breakpointManager.State,
                Lineage = _lineage,
                Metadata = BuildAgentMetadata(existing?.Metadata)
            };

            await _dependencies.Store.SaveInfoAsync(AgentId, info, cancellationToken);
        }
        catch
        {
            // best-effort meta tracking; ignore failures
        }
    }

    private int FindLastSfpIndex()
    {
        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            var message = _messages[i];
            if (message.Role == MessageRole.User) return i;
            if (message.Role == MessageRole.Assistant && !message.Content.OfType<ToolUseContent>().Any()) return i;
        }

        return -1;
    }

    private static int? TryReadInt(IReadOnlyDictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata == null || metadata.Count == 0) return null;
        if (!metadata.TryGetValue(key, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private async Task EnqueueMessageAsync(Message message, PendingKind kind, CancellationToken cancellationToken)
    {
        _messages.Add(message);
        if (kind == PendingKind.User)
        {
            // When the user provides new guidance, give the model a fresh chance (aligned with TS enqueueMessage()).
            _invalidToolArgsLastTool = "";
            _invalidToolArgsStreak = 0;
            _nextModelToolsOverride = null;
            _nextModelNudgeText = null;
            _iterationCount = 0;
        }
        await _hookManager.RunMessagesChangedAsync(_messages, cancellationToken);
    }

    private void EnsureProcessing()
    {
        CancellationTokenSource? ctsToCancel = null;
        lock (_processingLock)
        {
            if (_processingTask != null && !_processingTask.IsCompleted)
            {
                var now = NowMs();
                var elapsed = now - Interlocked.Read(ref _lastProcessingHeartbeatMs);
                var bp = _breakpointManager.State;

                // Waiting for approval is a valid paused state (aligned with TS ensureProcessing timeout rules).
                if (_runtimeState == AgentRuntimeState.Paused && bp == BreakpointState.AwaitingApproval)
                {
                    _processingQueued = true;
                    return;
                }

                // Long-running tools may legitimately exceed the processing timeout; rely on per-tool timeout instead.
                if (_runtimeState == AgentRuntimeState.Working && bp == BreakpointState.ToolExecuting)
                {
                    _processingQueued = true;
                    return;
                }

                if (elapsed > (long)ProcessingTimeout.TotalMilliseconds)
                {
                    _eventBus.EmitMonitor(new ErrorEvent
                    {
                        Type = "error",
                        Severity = "warn",
                        Phase = "system",
                        Message = "Processing timeout detected, forcing restart"
                    });

                    // Best-effort: cancel the current processing task and invalidate its runId so it can't race our new run.
                    ctsToCancel = _processingCts;
                    _processingCts = null;
                    _processingTask = null;
                    _processingRunId++;
                }
                else
                {
                    _processingQueued = true;
                    return;
                }
            }

            // Only start processing from READY. Otherwise queue a follow-up and return.
            if (_runtimeState != AgentRuntimeState.Ready)
            {
                _processingQueued = true;
                return;
            }

            _processingQueued = false;
            _processingRunId++;
            var runId = _processingRunId;

            var cts = _runCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_runCts.Token)
                : new CancellationTokenSource();
            var token = cts.Token;
            _processingCts = cts;
            TouchProcessingHeartbeat();

            // Start processing in the background. This mirrors TS ensureProcessing/runStep semantics, including queued reruns.
            _processingTask = Task.Run(async () =>
            {
                using var runActivity = KodeAgentActivitySource.Source.StartActivity("agent.run");
                runActivity?.SetTag("agent.session_type", _config.SessionType);
                runActivity?.SetTag("agent.role", _config.AgentRole);
                runActivity?.SetTag("agent.model", _config.Model);

                var runDimensions = new TagList
                {
                    { "model", _config.Model },
                    { "session_type", _config.SessionType },
                    { "agent_role", _config.AgentRole }
                };
                KodeAgentMetrics.RunsStarted.Add(1, runDimensions);
                var runStopwatch = Stopwatch.StartNew();

                var runAgain = false;
                try
                {
                    if (_runtimeState != AgentRuntimeState.Ready)
                    {
                        return;
                    }

                    TransitionState(AgentRuntimeState.Working);
                    while (RuntimeState == AgentRuntimeState.Working)
                    {
                        TouchProcessingHeartbeat();
                        var stepResult = await StepAsync(token);
                        if (!stepResult.HasMoreSteps) break;
                        if (RuntimeState == AgentRuntimeState.Paused) break;
                    }

                    runStopwatch.Stop();
                    KodeAgentMetrics.RunsCompleted.Add(1, runDimensions);
                    KodeAgentMetrics.RunDuration.Record(runStopwatch.Elapsed.TotalMilliseconds, runDimensions);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is expected when callers abort the run or we force-restart on timeout.
                    runActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "Cancelled");
                }
                catch (Exception ex)
                {
                    _eventBus.EmitMonitor(new ErrorEvent
                    {
                        Type = "error",
                        Severity = "error",
                        Phase = "system",
                        Message = ex.Message,
                        Detail = new { stack = ex.StackTrace }
                    });
                }
                finally
                {
                    var isCurrent = false;
                    lock (_processingLock)
                    {
                        // Avoid clearing state if this task is stale (e.g. timed-out and replaced).
                        if (_processingRunId != runId)
                        {
                            isCurrent = false;
                        }
                        else
                        {
                            isCurrent = true;

                            _processingCts = null;
                            _processingTask = null;

                            if (_processingQueued)
                            {
                                runAgain = true;
                                _processingQueued = false;
                            }
                        }
                    }

                    cts.Dispose();

                    if (isCurrent)
                    {
                        if (RuntimeState != AgentRuntimeState.Paused)
                        {
                            TransitionState(AgentRuntimeState.Ready);
                            _breakpointManager.TransitionTo(BreakpointState.Ready);
                            await SaveStateAsync();
                        }

                        if (runAgain)
                        {
                            EnsureProcessing();
                        }
                    }
                }
            }, token);
        }

        ctsToCancel?.Cancel();
        ctsToCancel?.Dispose();
    }

    private IReadOnlyDictionary<string, JsonElement> BuildAgentMetadata(IReadOnlyDictionary<string, JsonElement>? existing)
    {
        var merged = existing != null
            ? new Dictionary<string, JsonElement>(existing, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        merged["model"] = JsonSerializer.SerializeToElement(_config.Model, MetaJsonOptions);
        // TS-aligned: store tool descriptors in metadata.tools, plus a convenience list of tool ids.
        merged["tools"] = JsonSerializer.SerializeToElement(_tools.Select(t => t.ToDescriptor()).ToList(), MetaJsonOptions);
        merged["toolIds"] = JsonSerializer.SerializeToElement(_config.Tools ?? [], MetaJsonOptions);
        merged["sandboxOptions"] = JsonSerializer.SerializeToElement(_config.SandboxOptions, MetaJsonOptions);
        merged["sandboxConfig"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["kind"] = "local",
            ["workDir"] = _config.SandboxOptions?.WorkingDirectory,
            ["enforceBoundary"] = _config.SandboxOptions?.EnforceBoundary,
            ["allowPaths"] = _config.SandboxOptions?.AllowPaths,
            ["watchFiles"] = _config.SandboxOptions?.WatchFiles
        }, MetaJsonOptions);
        merged["permission"] = JsonSerializer.SerializeToElement(_config.Permissions, MetaJsonOptions);
        merged["todo"] = JsonSerializer.SerializeToElement(_config.Todo, MetaJsonOptions);
        merged["subagents"] = JsonSerializer.SerializeToElement(_config.SubAgents, MetaJsonOptions);
        merged["context"] = JsonSerializer.SerializeToElement(_config.Context, MetaJsonOptions);
        merged["skills"] = JsonSerializer.SerializeToElement(_config.Skills, MetaJsonOptions);
        merged["exposeThinking"] = JsonSerializer.SerializeToElement(_config.ExposeThinking, MetaJsonOptions);
        merged["maxIterations"] = JsonSerializer.SerializeToElement(_config.MaxIterations, MetaJsonOptions);
        merged["maxTokens"] = JsonSerializer.SerializeToElement(_config.MaxTokens, MetaJsonOptions);
        merged["temperature"] = JsonSerializer.SerializeToElement(_config.Temperature, MetaJsonOptions);
        merged["enableThinking"] = JsonSerializer.SerializeToElement(_config.EnableThinking, MetaJsonOptions);
        merged["thinkingBudget"] = JsonSerializer.SerializeToElement(_config.ThinkingBudget, MetaJsonOptions);
        merged["maxToolConcurrency"] = JsonSerializer.SerializeToElement(_config.MaxToolConcurrency, MetaJsonOptions);
        merged["toolTimeoutMs"] = JsonSerializer.SerializeToElement((int)_config.ToolTimeout.TotalMilliseconds, MetaJsonOptions);

        foreach (var kv in _metadata)
        {
            merged[kv.Key] = kv.Value;
        }

        return merged;
    }

    public async Task<Skill> ActivateSkillAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_skillsManager == null)
        {
            throw new InvalidOperationException("Skills not configured for this agent");
        }

        var skill = await _skillsManager.ActivateAsync(name, SkillActivationSource.Agent, cancellationToken);

        var instructionsXml = SkillsInjector.ToActivatedXml(skill);
        await RemindAsync(
            instructionsXml,
            category: "general",
            skipStandardEnding: true,
            cancellationToken: cancellationToken);

        if (skill.AllowedTools is { Count: > 0 })
        {
            _permissionManager.GrantTools(skill.AllowedTools);
        }

        _eventBus.EmitMonitor(new SkillActivatedEvent
        {
            Type = "skill_activated",
            Skill = skill.Name,
            ActivatedBy = "agent",
            Timestamp = NowMs()
        });

        return skill;
    }

    public async Task<DelegateTaskResult> DelegateTaskAsync(DelegateTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_dependencies.TemplateRegistry != null && !string.IsNullOrWhiteSpace(request.TemplateId))
        {
            if (!_dependencies.TemplateRegistry.TryGet(request.TemplateId, out _))
            {
                throw new InvalidOperationException($"Template not registered: {request.TemplateId}");
            }
        }

        var subAgentId = $"sub_{AgentId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}";

        var subConfig = new AgentConfig
        {
            TemplateId = request.TemplateId,
            Model = string.IsNullOrWhiteSpace(request.Model) ? _config.Model : request.Model!,
            SandboxOptions = _config.SandboxOptions,
            Tools = request.Tools,
            ExposeThinking = _config.ExposeThinking,
            MaxIterations = _config.MaxIterations,
            MaxTokens = _config.MaxTokens,
            Temperature = _config.Temperature,
            EnableThinking = _config.EnableThinking,
            ThinkingBudget = _config.ThinkingBudget,
            Context = _config.Context,
            MaxToolConcurrency = _config.MaxToolConcurrency,
            ToolTimeout = _config.ToolTimeout,
            Skills = _config.Skills
        };

        var subAgent = await CreateAsync(subAgentId, subConfig, _dependencies, cancellationToken);
        subAgent._lineage = [.. _lineage, AgentId];
        subAgent._metadata["parentAgentId"] = JsonSerializer.SerializeToElement(AgentId);
        subAgent._metadata["delegatedBy"] = JsonSerializer.SerializeToElement("task_tool");
        subAgent._metadata["parentCallId"] = JsonSerializer.SerializeToElement(request.CallId);

        _eventBus.EmitMonitor(new SubAgentCreatedEvent
        {
            Type = "subagent.created",
            CallId = request.CallId,
            AgentId = subAgentId,
            TemplateId = request.TemplateId,
            ParentAgentId = AgentId,
            Timestamp = NowMs()
        });

        CancellationTokenSource? forwardCts = null;
        Task? forwardTask = null;
        if (request.StreamEvents != false)
        {
            forwardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            forwardTask = ForwardSubAgentEventsAsync(subAgent, request, forwardCts.Token);
        }

        try
        {
            var run = await subAgent.RunAsync(request.Prompt, cancellationToken);

            var status = run.StopReason == StopReason.AwaitingApproval ? "paused" : "ok";
            var permissionIds = status == "paused"
                ? subAgent._permissionManager.GetPendingApprovalIds()
                : [];

            return new DelegateTaskResult
            {
                Status = status,
                Text = run.Response,
                PermissionIds = permissionIds,
                AgentId = subAgentId
            };
        }
        finally
        {
            if (forwardCts != null)
            {
                try { forwardCts.Cancel(); } catch { }
                forwardCts.Dispose();
            }

            if (forwardTask != null)
            {
                try { await forwardTask; } catch { }
            }

            await subAgent.DisposeAsync();
        }
    }

    public async Task<DelegateTaskResult> SpawnSubAgentAsync(
        string templateId,
        string prompt,
        SubAgentRuntime? runtime = null,
        CancellationToken cancellationToken = default)
    {
        if (_config.SubAgents == null)
        {
            throw new InvalidOperationException("Sub-agent configuration not enabled for this agent");
        }

        var remaining = runtime?.DepthRemaining ?? _config.SubAgents.Depth;
        if (remaining <= 0)
        {
            throw new InvalidOperationException("Sub-agent recursion limit reached");
        }

        if (_config.SubAgents.Templates != null &&
            _config.SubAgents.Templates.Count > 0 &&
            !_config.SubAgents.Templates.Contains(templateId))
        {
            throw new InvalidOperationException($"Template {templateId} not allowed for sub-agent");
        }

        var subAgentId = $"sub_{AgentId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}";

        // Build sub-agent config (aligned with TS spawnSubAgent: inherit sandbox/model, apply subagents overrides.permission).
        var permission = _config.SubAgents.Overrides?.Permission != null
            ? ConvertPermissionConfig(_config.SubAgents.Overrides.Permission)
            : _config.Permissions;

        var todo = _config.SubAgents.Overrides?.Todo ?? _config.Todo;

        var inheritSubAgents = _config.SubAgents.InheritConfig
            ? _config.SubAgents with { Depth = remaining - 1 }
            : null;

        var subConfig = new AgentConfig
        {
            TemplateId = templateId,
            Model = _config.Model,
            SandboxOptions = _config.SandboxOptions,
            ExposeThinking = _config.ExposeThinking,
            Permissions = permission,
            SubAgents = inheritSubAgents,
            Todo = todo,
            Context = _config.Context,
            MaxIterations = _config.MaxIterations,
            MaxTokens = _config.MaxTokens,
            Temperature = _config.Temperature,
            EnableThinking = _config.EnableThinking,
            ThinkingBudget = _config.ThinkingBudget,
            MaxToolConcurrency = _config.MaxToolConcurrency,
            ToolTimeout = _config.ToolTimeout,
            Skills = _config.Skills
        };

        var subAgent = await CreateAsync(subAgentId, subConfig, _dependencies, cancellationToken);
        subAgent._lineage = [.. _lineage, AgentId];
        subAgent._metadata["parentAgentId"] = JsonSerializer.SerializeToElement(AgentId);
        subAgent._metadata["delegatedBy"] = JsonSerializer.SerializeToElement("subagent");

        try
        {
            var run = await subAgent.RunAsync(prompt, cancellationToken);
            var status = run.StopReason == StopReason.AwaitingApproval ? "paused" : "ok";
            var permissionIds = status == "paused"
                ? subAgent._permissionManager.GetPendingApprovalIds()
                : [];

            return new DelegateTaskResult
            {
                Status = status,
                Text = run.Response,
                PermissionIds = permissionIds,
                AgentId = subAgentId
            };
        }
        finally
        {
            await subAgent.DisposeAsync();
        }
    }

    private static Kode.Agent.Sdk.Core.Types.PermissionConfig ConvertPermissionConfig(Kode.Agent.Sdk.Core.Templates.PermissionConfig config)
    {
        return new Kode.Agent.Sdk.Core.Types.PermissionConfig
        {
            Mode = config.Mode,
            AllowTools = config.AllowTools,
            RequireApprovalTools = config.RequireApprovalTools,
            DenyTools = config.DenyTools,
            Metadata = config.Metadata?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task ForwardSubAgentEventsAsync(Agent subAgent, DelegateTaskRequest request, CancellationToken cancellationToken)
    {
        var accumulatedText = new System.Text.StringBuilder();
        var latestStep = (int?)null;

        try
        {
            await foreach (var env in subAgent.EventBus.SubscribeAsync(EventChannel.Progress | EventChannel.Control, null, cancellationToken))
            {
                switch (env.Event)
                {
                    case TextChunkStartEvent start:
                        latestStep = start.Step;
                        break;

                    case TextChunkEvent textChunk:
                        {
                            latestStep = textChunk.Step;
                            var delta = textChunk.Delta ?? "";
                            if (delta.Length > 0)
                            {
                                accumulatedText.Append(delta);
                                _eventBus.EmitMonitor(new SubAgentDeltaEvent
                                {
                                    Type = "subagent.delta",
                                    SubAgentId = subAgent.AgentId,
                                    TemplateId = request.TemplateId,
                                    CallId = request.CallId,
                                    Delta = delta,
                                    Text = accumulatedText.ToString(),
                                    Step = latestStep,
                                    Timestamp = NowMs()
                                });
                            }
                            break;
                        }

                    case ThinkChunkStartEvent thinkStart:
                        latestStep = thinkStart.Step;
                        break;

                    case ThinkChunkEvent thinkChunk:
                        {
                            latestStep = thinkChunk.Step;
                            var delta = thinkChunk.Delta ?? "";
                            if (delta.Length > 0)
                            {
                                _eventBus.EmitMonitor(new SubAgentThinkingEvent
                                {
                                    Type = "subagent.thinking",
                                    SubAgentId = subAgent.AgentId,
                                    TemplateId = request.TemplateId,
                                    CallId = request.CallId,
                                    Delta = delta,
                                    Step = latestStep,
                                    Timestamp = NowMs()
                                });
                            }
                            break;
                        }

                    case ToolStartEvent toolStart:
                        {
                            var call = toolStart.Call;
                            _eventBus.EmitMonitor(new SubAgentToolStartEvent
                            {
                                Type = "subagent.tool_start",
                                SubAgentId = subAgent.AgentId,
                                TemplateId = request.TemplateId,
                                ParentCallId = request.CallId,
                                ToolCallId = call.Id,
                                ToolName = call.Name,
                                InputPreview = BuildInputPreview(call.InputPreview),
                                Timestamp = NowMs()
                            });
                            break;
                        }

                    case ToolEndEvent toolEnd:
                        {
                            var call = toolEnd.Call;
                            _eventBus.EmitMonitor(new SubAgentToolEndEvent
                            {
                                Type = "subagent.tool_end",
                                SubAgentId = subAgent.AgentId,
                                TemplateId = request.TemplateId,
                                ParentCallId = request.CallId,
                                ToolCallId = call.Id,
                                ToolName = call.Name,
                                DurationMs = call.DurationMs,
                                IsError = call.IsError ?? false,
                                Timestamp = NowMs()
                            });
                            break;
                        }

                    case PermissionRequiredEvent permissionRequired:
                        {
                            var call = permissionRequired.Call;
                            _eventBus.EmitMonitor(new SubAgentPermissionRequiredEvent
                            {
                                Type = "subagent.permission_required",
                                SubAgentId = subAgent.AgentId,
                                TemplateId = request.TemplateId,
                                ParentCallId = request.CallId,
                                ToolCallId = call.Id,
                                ToolName = call.Name,
                                Timestamp = NowMs()
                            });
                            break;
                        }

                    case DoneEvent:
                        return;
                }
            }
        }
        catch
        {
            // best-effort: ignore sub-agent iteration failures/cancellation
        }
    }

    private static string? BuildInputPreview(object? args)
    {
        if (args == null) return null;

        try
        {
            var json = args is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(args);
            json = json.Replace("\n", " ").Replace("\r", " ");
            return json.Length <= 280 ? json : json[..280] + "...";
        }
        catch
        {
            return null;
        }
    }

    private async Task InitializeSkillsAsync(CancellationToken cancellationToken)
    {
        if (_skillsManager != null) return;
        if (_config.Skills == null) return;
        if (_sandbox == null) return;

        _skillsManager = new SkillsManager(
            _config.Skills,
            _sandbox,
            _dependencies.Store,
            AgentId,
            _dependencies.LoggerFactory?.CreateLogger<SkillsManager>());

        try
        {
            await _skillsManager.RestoreStateAsync(cancellationToken);
        }
        catch
        {
            // best-effort: skills state is optional
        }

        var skills = await _skillsManager.DiscoverAsync(cancellationToken);
        if (skills.Count == 0) return;

        var skillsXml = SkillsInjector.ToPromptXml(skills);
        if (!string.IsNullOrWhiteSpace(skillsXml))
        {
            _systemPrompt = (_systemPrompt ?? string.Empty) + skillsXml;
        }

        _eventBus.EmitMonitor(new SkillDiscoveredEvent
        {
            Type = "skill_discovered",
            Skills = skills.Select(s => s.Name).ToList(),
            Timestamp = NowMs()
        });

        // 1. SkillsConfig.AutoActivate (KodaClaw config path, does not require Template system)
        if (_config.Skills?.AutoActivate is { Count: > 0 })
        {
            var autoActivated = await _skillsManager.AutoActivateAsync(_config.Skills.AutoActivate, cancellationToken);
            if (autoActivated.Count > 0)
            {
                foreach (var skill in autoActivated)
                {
                    var xml = SkillsInjector.ToActivatedXml(skill);
                    await RemindAsync(xml, category: "general", skipStandardEnding: false, cancellationToken: cancellationToken);
                }

                var granted1 = autoActivated
                    .SelectMany(s => s.AllowedTools ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                _permissionManager.GrantTools(granted1);

                _eventBus.EmitMonitor(new SkillActivatedEvent
                {
                    Type = "skill_activated",
                    Skill = string.Join(", ", autoActivated.Select(s => s.Name)),
                    ActivatedBy = "auto",
                    Timestamp = NowMs()
                });
            }
        }

        // 2. Template runtime skills behavior (autoActivate / recommend).
        TemplateSkillsConfig? templateSkills = null;
        if (_dependencies.TemplateRegistry != null &&
            !string.IsNullOrWhiteSpace(_config.TemplateId) &&
            _dependencies.TemplateRegistry.TryGet(_config.TemplateId!, out var template) &&
            template?.Runtime?.Skills != null)
        {
            templateSkills = template.Runtime.Skills;
        }

        if (templateSkills?.AutoActivate is { Count: > 0 })
        {
            var autoActivated = await _skillsManager.AutoActivateAsync(templateSkills.AutoActivate, cancellationToken);
            if (autoActivated.Count > 0)
            {
                foreach (var skill in autoActivated)
                {
                    var xml = SkillsInjector.ToActivatedXml(skill);
                    await RemindAsync(xml, category: "general", skipStandardEnding: false, cancellationToken: cancellationToken);
                }

                var granted2 = autoActivated
                    .SelectMany(s => s.AllowedTools ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                _permissionManager.GrantTools(granted2);

                _eventBus.EmitMonitor(new SkillActivatedEvent
                {
                    Type = "skill_activated",
                    Skill = string.Join(", ", autoActivated.Select(s => s.Name)),
                    ActivatedBy = "auto",
                    Timestamp = NowMs()
                });
            }
        }

        if (templateSkills?.Recommend is { Count: > 0 })
        {
            var recommended = templateSkills.Recommend
                .Where(name => _skillsManager.Get(name) != null && !_skillsManager.IsActivated(name))
                .ToList();

            if (recommended.Count > 0)
            {
                var recommendXml =
                    $"\n<recommended_skills>\nConsider activating these skills if relevant to your task: {string.Join(", ", recommended)}\n</recommended_skills>\n";
                _systemPrompt = (_systemPrompt ?? string.Empty) + recommendXml;
            }
        }
    }

    private static string WrapReminder(string content, bool skipStandardEnding)
    {
        if (skipStandardEnding) return content;
        return string.Join("\n", new[]
        {
            "<system-reminder>",
            content,
            "",
            "This is a system reminder. DO NOT respond to this message directly.",
            "DO NOT mention this reminder to the user.",
            "Continue with your current task.",
            "</system-reminder>"
        });
    }

    private async Task RemindAsync(
        string content,
        string category,
        bool skipStandardEnding,
        CancellationToken cancellationToken)
    {
        var payload = WrapReminder(content, skipStandardEnding);

        _messageQueue.Send(content, new SendOptions
        {
            Kind = PendingKind.Reminder,
            Reminder = new ReminderOptions
            {
                SkipStandardEnding = skipStandardEnding,
                Category = category
            }
        });
        await _messageQueue.FlushAsync(cancellationToken);

        _eventBus.EmitMonitor(new ReminderSentEvent
        {
            Type = "reminder_sent",
            Category = category,
            Content = payload
        });
    }

    private void InitializeToolServices()
    {
        if (_sandbox == null) return;
        if (_filePool != null) return;

        var watch = _config.SandboxOptions?.WatchFiles ?? true;
        _filePool = new FilePool(
            _sandbox,
            new FilePoolOptions
            {
                Watch = watch,
                OnChange = e => HandleExternalFileChange(e.Path, e.Mtime)
            },
            _dependencies.LoggerFactory?.CreateLogger<FilePool>());
        _toolServices = new ToolServices(_filePool);
    }

    private async Task InitializeTodoAsync(CancellationToken cancellationToken)
    {
        if (_todoManager != null) return;
        if (_config.Todo?.Enabled != true) return;

        _todoService = new TodoService(_dependencies.Store, AgentId, _dependencies.LoggerFactory?.CreateLogger<TodoService>());
        await _todoService.LoadAsync(cancellationToken);

        _todoManager = new TodoManager(
            _todoService,
            _config.Todo,
            _eventBus,
            (content, category, ct) => RemindAsync(content, category, skipStandardEnding: false, cancellationToken: ct),
            _dependencies.LoggerFactory?.CreateLogger<TodoManager>());

        _todoManager.HandleStartup(cancellationToken);
    }

    private async Task InitializeToolManualAsync(CancellationToken cancellationToken)
    {
        if (_sandbox == null) return;
        if (_tools.Count == 0) return;

        var prompts = new List<(string Name, string Prompt)>();
        foreach (var tool in _tools)
        {
            var context = new ToolContext
            {
                AgentId = AgentId,
                CallId = "manual",
                Sandbox = _sandbox,
                Agent = this,
                Services = _toolServices,
                CancellationToken = cancellationToken
            };

            var prompt = await tool.GetPromptAsync(context);
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                prompts.Add((tool.Name, prompt.Trim()));
            }
        }

        if (prompts.Count == 0) return;

        var manual = RenderToolManual(prompts);
        _systemPrompt = (_systemPrompt ?? string.Empty) + manual;

        _eventBus.EmitMonitor(new ToolManualUpdatedEvent
        {
            Type = "tool_manual_updated",
            Tools = prompts.Select(p => p.Name).ToList(),
            Timestamp = NowMs()
        });
    }

    private static string RenderToolManual(IReadOnlyList<(string Name, string Prompt)> prompts)
    {
        var parts = new List<string>
        {
            "\n<tool_manual>\n"
        };
        foreach (var (name, prompt) in prompts)
        {
            parts.Add($"<tool name=\"{name}\">\n{prompt}\n</tool>\n");
        }
        parts.Add("</tool_manual>\n");
        return string.Join("\n", parts);
    }

    private void HandleExternalFileChange(string path, long mtime)
    {
        if (_sandbox == null) return;

        var rel = Path.GetRelativePath(_sandbox.WorkingDirectory, path);
        _eventBus.EmitMonitor(new FileChangedEvent
        {
            Type = "file_changed",
            Path = rel,
            Mtime = mtime
        });

        // Only enqueue — do NOT call FlushAsync here. FileSystemWatcher callbacks run on a
        // thread-pool thread; flushing would call _messages.Add() on that thread while the
        // agent's processing loop may be enumerating _messages (e.g. in ContextManager.Analyze),
        // causing "Collection was modified; enumeration operation may not execute."
        // The queue is drained at the start of every StepAsync (line ~1079), so the reminder
        // will be delivered before the next model call without any cross-thread mutation.
        var reminder = $"检测到外部修改：{rel}。请重新使用 fs_read 确认文件内容，并在必要时向用户同步。";
        _messageQueue.Send(reminder, new SendOptions
        {
            Kind = PendingKind.Reminder,
            Reminder = new ReminderOptions { Category = "file", SkipStandardEnding = false }
        });
    }

    private static EventChannel ParseChannels(IReadOnlyList<string>? channels)
    {
        if (channels == null || channels.Count == 0) return EventChannel.All;

        var flags = (EventChannel)0;
        foreach (var c in channels)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var norm = c.Trim().ToLowerInvariant();
            flags |= norm switch
            {
                "progress" => EventChannel.Progress,
                "control" => EventChannel.Control,
                "monitor" => EventChannel.Monitor,
                "all" => EventChannel.All,
                _ => throw new ArgumentException($"Unknown channel: {c}", nameof(channels))
            };
        }

        return flags == 0 ? EventChannel.All : flags;
    }

    private sealed record SealPayload(object Payload, string Message);

    private SealPayload BuildSealPayload(string state, string toolUseId, string fallbackNote, ToolCallRecord? record = null)
    {
        var baseMessage = state switch
        {
            "APPROVAL_REQUIRED" => "工具在等待审批时会话中断，系统已自动封口。",
            "APPROVED" => "工具已通过审批但尚未执行，系统已自动封口。",
            "EXECUTING" => "工具执行过程中会话中断，系统已自动封口。",
            "PENDING" => "工具刚准备执行时会话中断，系统已自动封口。",
            _ => fallbackNote
        };

        var recommendations = state switch
        {
            "APPROVAL_REQUIRED" => new[] { "确认审批是否仍然需要", "如需继续，请重新触发工具并完成审批" },
            "APPROVED" => new[] { "确认工具输入是否仍然有效", "如需执行，请重新触发工具" },
            "EXECUTING" => new[] { "检查工具可能产生的副作用", "确认外部系统状态后再重试" },
            "PENDING" => new[] { "确认工具参数是否正确", "再次触发工具以继续流程" },
            _ => new[] { "检查封口说明并决定是否重试工具" }
        };

        var detail = new Dictionary<string, object?>
        {
            ["status"] = state,
            ["startedAt"] = record?.StartedAt,
            ["approval"] = record?.Approval,
            ["toolId"] = toolUseId,
            ["note"] = baseMessage
        };

        var payload = new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = baseMessage,
            ["data"] = detail,
            ["recommendations"] = recommendations
        };

        return new SealPayload(payload, baseMessage);
    }

    private void SealNonTerminalToolRecords(string note)
    {
        var terminal = new HashSet<ToolCallState>
        {
            ToolCallState.Completed,
            ToolCallState.Failed,
            ToolCallState.Denied,
            ToolCallState.Sealed
        };

        foreach (var record in _toolRunner.ActiveToolCalls)
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

            var sealedPayload = BuildSealPayload(state, record.Id, note, record);
            _toolRunner.SealToolCall(record.Id, sealedPayload.Message, sealedPayload.Payload);
        }
    }

    private async Task<IReadOnlyList<ToolCallSnapshot>> AutoSealDanglingToolUsesAsync(string reason, CancellationToken cancellationToken)
    {
        var toolResultIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var msg in _messages.Where(m => m.Role == MessageRole.User))
        {
            foreach (var res in msg.Content.OfType<ToolResultContent>())
            {
                toolResultIds.Add(res.ToolUseId);
            }
        }

        var sealedSnapshots = new List<ToolCallSnapshot>();
        var insertions = new List<(int Index, List<ContentBlock> Blocks)>();
        var alreadyInserted = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < _messages.Count; i++)
        {
            var msg = _messages[i];
            if (msg.Role != MessageRole.Assistant) continue;

            var localToolUses = msg.Content.OfType<ToolUseContent>().ToList();
            if (localToolUses.Count == 0) continue;

            var blocks = new List<ContentBlock>();
            foreach (var use in localToolUses)
            {
                if (alreadyInserted.Contains(use.Id)) continue;
                if (toolResultIds.Contains(use.Id)) continue;

                _toolRunner.RegisterToolCall(use.Id, use.Name, use.Input);
                var existing = _toolRunner.GetToolCall(use.Id);
                var sealedPayload = BuildSealPayload("TOOL_RESULT_MISSING", use.Id, reason, existing);
                _toolRunner.SealToolCall(use.Id, sealedPayload.Message, sealedPayload.Payload);
                var snapshot = _toolRunner.GetSnapshot(use.Id);
                if (snapshot != null) sealedSnapshots.Add(snapshot);

                blocks.Add(new ToolResultContent
                {
                    ToolUseId = use.Id,
                    Content = sealedPayload.Payload,
                    IsError = true
                });
                alreadyInserted.Add(use.Id);
                toolResultIds.Add(use.Id);
            }

            if (blocks.Count > 0)
            {
                insertions.Add((i + 1, blocks));
            }
        }

        if (insertions.Count == 0) return sealedSnapshots;

        for (var k = insertions.Count - 1; k >= 0; k--)
        {
            var ins = insertions[k];
            _messages.Insert(ins.Index, new Message { Role = MessageRole.User, Content = ins.Blocks });
        }

        await _hookManager.RunMessagesChangedAsync(_messages, cancellationToken);
        return sealedSnapshots;
    }

    private async Task<int> SanitizeOrphanToolResultsAsync(
        CancellationToken cancellationToken,
        string note = "Sanitized orphan tool_result blocks (missing tool_use).")
    {
        var toolUseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var msg in _messages.Where(m => m.Role == MessageRole.Assistant))
        {
            foreach (var use in msg.Content.OfType<ToolUseContent>())
            {
                toolUseIds.Add(use.Id);
            }
        }

        var changedAny = false;
        var converted = 0;
        for (var i = 0; i < _messages.Count; i++)
        {
            var msg = _messages[i];
            if (msg.Role != MessageRole.User) continue;

            var changed = false;
            var next = new List<ContentBlock>();
            foreach (var block in msg.Content)
            {
                if (block is ToolResultContent tr && !toolUseIds.Contains(tr.ToolUseId))
                {
                    changed = true;
                    converted++;
                    var preview = PreviewToolResult(tr.Content, 1400);
                    next.Add(new TextContent
                    {
                        Text = $"[tool_result orphaned] tool_use_id={tr.ToolUseId}{(tr.IsError ? " (error)" : "")}\n{preview}"
                    });
                }
                else
                {
                    next.Add(block);
                }
            }

            if (changed)
            {
                changedAny = true;
                _messages[i] = msg with { Content = next };
            }
        }

        if (changedAny)
        {
            try
            {
                await _dependencies.Store.SaveMessagesAsync(AgentId, _messages, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to persist messages after context repair");
            }

            _eventBus.EmitMonitor(new ContextRepairEvent
            {
                Type = "context_repair",
                Reason = "orphan_tool_result",
                Converted = converted,
                Note = note
            });
        }

        return converted;
    }

    /// <summary>
    /// Removes trailing user turns that were flushed but never answered by the model.
    /// These arise when a run fails (e.g. empty response, network error) after
    /// <see cref="MessageQueue.FlushAsync"/> has already appended the user message but
    /// before an assistant response was produced.  If not cleaned up, every subsequent
    /// request carries the un-answered turn, causing models to see consecutive user
    /// messages and often producing more empty or malformed responses.
    ///
    /// Rule: collect non-tool-result User messages that appear after the last Assistant
    /// message.  If there are two or more, the earlier ones are dangling — remove them
    /// and keep only the latest (the current request that was just flushed).
    /// Tool-result messages (User role, <see cref="ToolResultContent"/> blocks) are
    /// excluded to avoid breaking tool_use / tool_result pairing.
    /// </summary>
    /// <returns>Number of messages removed.</returns>
    private int SanitizeDanglingUserTurns()
    {
        // Index of the first message after the last assistant response (0 when no assistant exists).
        var afterLastAssistant = _messages.FindLastIndex(m => m.Role == MessageRole.Assistant) + 1;

        // Collect indices of non-tool-result User messages after the last assistant.
        var candidateIndices = new List<int>();
        for (var i = afterLastAssistant; i < _messages.Count; i++)
        {
            var msg = _messages[i];
            if (msg.Role == MessageRole.User && !msg.Content.Any(c => c is ToolResultContent))
                candidateIndices.Add(i);
        }

        // Need at least 2: the dangling one(s) + the current request (keep the last, remove the rest).
        if (candidateIndices.Count < 2) return 0;

        // Remove in reverse-index order to keep indices valid.
        var toRemove = candidateIndices
            .Take(candidateIndices.Count - 1)
            .OrderByDescending(i => i)
            .ToList();

        foreach (var idx in toRemove)
            _messages.RemoveAt(idx);

        return toRemove.Count;
    }

    private static string PreviewToolResult(object? value, int limit)
    {
        try
        {
            var text = value is string s ? s : System.Text.Json.JsonSerializer.Serialize(value);
            return text.Length > limit ? text[..limit] + "…" : text;
        }
        catch
        {
            var text = value?.ToString() ?? "";
            return text.Length > limit ? text[..limit] + "…" : text;
        }
    }

    private sealed class ToolServices(FilePool filePool) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(FilePool)) return filePool;
            if (serviceType == typeof(IFilePool)) return filePool;
            return null;
        }
    }
}
