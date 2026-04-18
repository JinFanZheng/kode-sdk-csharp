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
public sealed partial class Agent : IAgent, ISkillsAwareAgent, ITaskDelegatorAgent, ISubAgentSpawnerAgent
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
    private readonly System.Threading.Lock _stateLock = new();
    private CancellationTokenSource? _runCts;
    private readonly System.Threading.Lock _processingLock = new();
    private Task? _processingTask;
    private CancellationTokenSource? _processingCts;
    private bool _processingQueued;
    private long _processingRunId;
    private long _lastProcessingHeartbeatMs;
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(5);
    private readonly System.Threading.Lock _activeToolCallsLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _activeToolCalls = new(StringComparer.Ordinal);
    private readonly System.Threading.Lock _onSubscriptionsLock = new();
    private readonly List<CancellationTokenSource> _onSubscriptions = [];
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
        private readonly Agent? _owner;

        public CancellationDisposable(CancellationTokenSource cts, Agent? owner = null)
        {
            _cts = cts;
            _owner = owner;
        }

        public void Dispose()
        {
            var cts = Interlocked.Exchange(ref _cts, null);
            if (cts == null) return;
            if (_owner != null)
            {
                lock (_owner._onSubscriptionsLock)
                {
                    _owner._onSubscriptions.Remove(cts);
                }
            }
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

}
