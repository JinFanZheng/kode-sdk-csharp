using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Todo;
using System.Text.Json.Serialization;

namespace Kode.Agent.Sdk.Core.Abstractions;

/// <summary>
/// Represents the runtime state of an agent.
/// </summary>
[JsonConverter(typeof(AgentRuntimeStateJsonConverter))]
public enum AgentRuntimeState
{
    /// <summary>Agent is ready to receive input.</summary>
    Ready,
    /// <summary>Agent is actively processing.</summary>
    Working,
    /// <summary>Agent is paused (e.g., waiting for approval).</summary>
    Paused
}

/// <summary>
/// Represents the breakpoint state for crash recovery.
/// </summary>
[JsonConverter(typeof(BreakpointStateJsonConverter))]
public enum BreakpointState
{
    /// <summary>Initial state, ready to start.</summary>
    Ready,
    /// <summary>About to call the model.</summary>
    PreModel,
    /// <summary>Streaming response from model.</summary>
    StreamingModel,
    /// <summary>Tool calls pending execution.</summary>
    ToolPending,
    /// <summary>Waiting for user approval.</summary>
    AwaitingApproval,
    /// <summary>About to execute tool.</summary>
    PreTool,
    /// <summary>Tool is executing.</summary>
    ToolExecuting,
    /// <summary>Tool execution completed.</summary>
    PostTool
}

/// <summary>
/// Per-run options that override agent config for a single RunAsync call.
/// </summary>
public record AgentRunOptions
{
    /// <summary>Whether to enable extended thinking (overrides AgentConfig.EnableThinking).</summary>
    public bool? EnableThinking { get; init; }

    /// <summary>Thinking token budget (overrides AgentConfig.ThinkingBudget).</summary>
    public int? ThinkingBudget { get; init; }

    /// <summary>Whether to expose thinking events/content (overrides AgentConfig.ExposeThinking).</summary>
    public bool? ExposeThinking { get; init; }
}

/// <summary>
/// Core agent interface for managing agent lifecycle.
/// </summary>
public interface IAgent : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier of this agent.
    /// </summary>
    string AgentId { get; }

    /// <summary>
    /// Gets the current runtime state.
    /// </summary>
    AgentRuntimeState RuntimeState { get; }

    /// <summary>
    /// Gets the current breakpoint state for recovery.
    /// </summary>
    BreakpointState BreakpointState { get; }

    /// <summary>
    /// Gets the number of completed agent loop steps.
    /// </summary>
    int StepCount { get; }

    /// <summary>
    /// Gets the event bus for subscribing to agent events.
    /// </summary>
    IEventBus EventBus { get; }

    /// <summary>
    /// Runs the agent loop until completion or pause.
    /// </summary>
    /// <param name="input">The user input message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final assistant response.</returns>
    Task<AgentRunResult> RunAsync(string input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the agent loop with per-run options that override the agent configuration.
    /// </summary>
    /// <param name="input">The user input message.</param>
    /// <param name="options">Per-run options (think, budget, etc.); null to use defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final assistant response.</returns>
    Task<AgentRunResult> RunAsync(string input, AgentRunOptions? options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the agent loop with multi-modal content (text + images) until completion or pause.
    /// </summary>
    /// <param name=\parts\>The content blocks (text, images, etc.) to send as user input.</param>
    /// <param name=\cancellationToken\>Cancellation token.</param>
    /// <returns>The final assistant response.</returns>
    Task<AgentRunResult> RunAsync(IReadOnlyList<Kode.Agent.Sdk.Core.Types.ContentBlock> parts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the agent loop with multi-modal content and per-run options.
    /// </summary>
    /// <param name="parts">The content blocks (text, images, etc.) to send as user input.</param>
    /// <param name="options">Per-run options (think, budget, etc.); null to use defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final assistant response.</returns>
    Task<AgentRunResult> RunAsync(IReadOnlyList<Kode.Agent.Sdk.Core.Types.ContentBlock> parts, AgentRunOptions? options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a single step of the agent loop.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Step result indicating what happened.</returns>
    Task<AgentStepResult> StepAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses the agent at the current state.
    /// </summary>
    Task PauseAsync();

    /// <summary>
    /// Resumes the agent from a paused state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a pending tool call.
    /// </summary>
    /// <param name="callId">The tool call ID to approve.</param>
    Task ApproveToolCallAsync(string callId);

    /// <summary>
    /// Denies a pending tool call.
    /// </summary>
    /// <param name="callId">The tool call ID to deny.</param>
    /// <param name="reason">Optional reason for denial.</param>
    Task DenyToolCallAsync(string callId, string? reason = null);

    /// <summary>
    /// Creates a snapshot (safe fork point) of the current agent messages (TS-aligned <c>agent.snapshot(label?)</c>).
    /// </summary>
    /// <param name="label">Optional snapshot id (TS uses <c>sfp:{lastSfpIndex}</c> when omitted).</param>
    /// <returns>The snapshot id.</returns>
    Task<string> SnapshotAsync(string? label = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forks this agent to create a new agent with copied state.
    /// </summary>
    /// <param name="newAgentId">The ID for the forked agent.</param>
    /// <param name="snapshotId">Optional snapshot id to fork from (when null, a new snapshot is created first).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The forked agent.</returns>
    Task<IAgent> ForkAsync(string newAgentId, CancellationToken cancellationToken = default, string? snapshotId = null);

    /// <summary>
    /// Gets the unique identifier of this agent.
    /// </summary>
    string Id => AgentId;

    /// <summary>
    /// Gets the current todo list.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of todo items.</returns>
    Task<IReadOnlyList<TodoItem>> GetTodosAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the todo list (replaces all existing todos).
    /// </summary>
    /// <param name="todos">The new todo list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetTodosAsync(IEnumerable<TodoItem> todos, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all compressed history windows saved for this session.
    /// Each window represents a snapshot of the conversation at the time of a context compression.
    /// Used by <c>history_search</c> to search across compressed history for relevant context.
    /// </summary>
    Task<IReadOnlyList<HistoryWindow>> GetHistoryWindowsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces an immediate context compression regardless of current token usage.
    /// Returns <c>true</c> when compression succeeded and messages were replaced;
    /// <c>false</c> when the compressor returned null (e.g. no summarizer available).
    /// </summary>
    Task<bool> ForceCompressAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of running the agent.
/// </summary>
public record AgentRunResult
{
    /// <summary>Whether the agent completed successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>The final text response, if any.</summary>
    public string? Response { get; init; }

    /// <summary>The reason for stopping.</summary>
    public required StopReason StopReason { get; init; }

    /// <summary>Total tokens used in this run.</summary>
    public TokenUsage? TokenUsage { get; init; }

    /// <summary>Error message when StopReason is Error.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of a single agent step.
/// </summary>
public record AgentStepResult
{
    /// <summary>The type of step that was executed.</summary>
    public required StepType StepType { get; init; }

    /// <summary>Whether there are more steps to execute.</summary>
    public required bool HasMoreSteps { get; init; }

    /// <summary>Tool calls made in this step, if any.</summary>
    public IReadOnlyList<ToolCallInfo>? ToolCalls { get; init; }
}

/// <summary>
/// Reason for agent stopping.
/// </summary>
public enum StopReason
{
    /// <summary>Completed normally.</summary>
    EndTurn,
    /// <summary>Maximum iterations reached.</summary>
    MaxIterations,
    /// <summary>Paused for approval.</summary>
    AwaitingApproval,
    /// <summary>User cancelled.</summary>
    Cancelled,
    /// <summary>Error occurred.</summary>
    Error
}

/// <summary>
/// Type of step executed.
/// </summary>
public enum StepType
{
    /// <summary>Called the model.</summary>
    ModelCall,
    /// <summary>Executed tools.</summary>
    ToolExecution,
    /// <summary>Waiting for approval.</summary>
    AwaitingApproval
}

/// <summary>
/// Token usage statistics.
/// </summary>
public record TokenUsage
{
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    /// Number of prompt tokens that were read from cache (cache hit).
    /// Populated by providers that track prefix caching (DeepSeek, Anthropic).
    /// Null when the provider does not report cache tokens.
    /// </summary>
    public int? CacheHitTokens { get; init; }

    /// <summary>
    /// Number of prompt tokens that were NOT read from cache (cache miss).
    /// Populated by providers that track prefix caching (DeepSeek, Anthropic).
    /// Null when the provider does not report cache tokens.
    /// </summary>
    public int? CacheMissTokens { get; init; }
}

/// <summary>
/// Information about a tool call.
/// </summary>
public record ToolCallInfo
{
    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public required object Arguments { get; init; }
    public required ToolCallState State { get; init; }
}

/// <summary>
/// State of a tool call.
/// </summary>
[JsonConverter(typeof(ToolCallStateJsonConverter))]
public enum ToolCallState
{
    Pending,
    ApprovalRequired,
    Approved,
    Executing,
    Completed,
    Failed,
    Denied,
    Sealed
}
