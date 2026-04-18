using System.Text.Json;
using System.Text.Json.Serialization;
using Kode.Agent.Sdk.Core.Todo;

namespace Kode.Agent.Sdk.Core.Abstractions;

/// <summary>
/// Event channels for the three-channel event system.
/// </summary>
[Flags]
public enum EventChannel
{
    /// <summary>Progress events for UI streaming.</summary>
    Progress = 1,
    /// <summary>Control events for approval flow.</summary>
    Control = 2,
    /// <summary>Monitor events for observability.</summary>
    Monitor = 4,
    /// <summary>All channels.</summary>
    All = Progress | Control | Monitor
}

/// <summary>
/// Event bus interface for the three-channel event system.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Gets the most recent bookmark emitted by this bus (if any).
    /// </summary>
    Bookmark? GetLastBookmark();

    /// <summary>
    /// Gets the current cursor position (TS-aligned events.getCursor()).
    /// </summary>
    long GetCursor();

    /// <summary>
    /// Subscribes to events from specified channels.
    /// </summary>
    /// <param name="channels">Channels to subscribe to.</param>
    /// <param name="since">Optional bookmark to resume from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable of event envelopes.</returns>
    /// <example>
    /// <code>
    /// await foreach (var env in agent.EventBus.SubscribeAsync(EventChannel.Progress, cancellationToken: ct))
    /// {
    ///     switch (env.Event)
    ///     {
    ///         case TextChunkEvent chunk: Console.Write(chunk.Delta); break;
    ///         case ToolStartEvent ts:    Console.WriteLine($"[tool] {ts.Call.Name}"); break;
    ///         case DoneEvent:            return;
    ///     }
    /// }
    /// </code>
    /// </example>
    IAsyncEnumerable<EventEnvelope> SubscribeAsync(
        EventChannel channels = EventChannel.All,
        Bookmark? since = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to events from specified channels, optionally filtering by event <c>type</c> values (TS-aligned kinds filter).
    /// </summary>
    IAsyncEnumerable<EventEnvelope> SubscribeAsync(
        EventChannel channels,
        Bookmark? since,
        IReadOnlyCollection<string>? kinds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to progress events only.
    /// </summary>
    IAsyncEnumerable<EventEnvelope<ProgressEvent>> SubscribeProgressAsync(
        Bookmark? since = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to progress events only, optionally filtering by event <c>type</c> values (TS-aligned kinds filter).
    /// </summary>
    IAsyncEnumerable<EventEnvelope<ProgressEvent>> SubscribeProgressAsync(
        Bookmark? since,
        IReadOnlyCollection<string>? kinds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a handler for control events.
    /// </summary>
    /// <typeparam name="TEvent">The control event type.</typeparam>
    /// <param name="handler">The event handler.</param>
    /// <returns>Dispose to unsubscribe.</returns>
    IDisposable OnControl<TEvent>(Action<TEvent> handler) where TEvent : ControlEvent;

    /// <summary>
    /// Registers a handler for monitor events.
    /// </summary>
    /// <typeparam name="TEvent">The monitor event type.</typeparam>
    /// <param name="handler">The event handler.</param>
    /// <returns>Dispose to unsubscribe.</returns>
    IDisposable OnMonitor<TEvent>(Action<TEvent> handler) where TEvent : MonitorEvent;

    /// <summary>
    /// Emits a progress event.
    /// </summary>
    EventEnvelope<TEvent> EmitProgress<TEvent>(TEvent @event) where TEvent : ProgressEvent;

    /// <summary>
    /// Emits a control event.
    /// </summary>
    EventEnvelope<TEvent> EmitControl<TEvent>(TEvent @event) where TEvent : ControlEvent;

    /// <summary>
    /// Emits a monitor event.
    /// </summary>
    EventEnvelope<TEvent> EmitMonitor<TEvent>(TEvent @event) where TEvent : MonitorEvent;

    /// <summary>
    /// TS-aligned: returns the number of critical events currently buffered in memory due to persistence failures.
    /// </summary>
    int GetFailedEventCount();

    /// <summary>
    /// TS-aligned: keeps retrying buffered critical events until the buffer is empty (or cancellation is requested).
    /// </summary>
    Task FlushFailedEventsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Bookmark for event stream position.
/// </summary>
public record Bookmark
{
    public required long Seq { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Envelope wrapping an event with metadata.
/// </summary>
public record EventEnvelope
{
    public required long Cursor { get; init; }
    public required Bookmark Bookmark { get; init; }
    public required AgentEvent Event { get; init; }
}

/// <summary>
/// Typed envelope wrapping a specific event type.
/// </summary>
public record EventEnvelope<TEvent> where TEvent : AgentEvent
{
    public required long Cursor { get; init; }
    public required Bookmark Bookmark { get; init; }
    public required TEvent Event { get; init; }

    /// <summary>
    /// Converts to base EventEnvelope.
    /// </summary>
    public EventEnvelope ToEnvelope() => new()
    {
        Cursor = Cursor,
        Bookmark = Bookmark,
        Event = Event
    };
}

/// <summary>
/// Base class for all agent events.
/// </summary>
/// </summary>
public abstract record AgentEvent
{
    /// <summary>
    /// TS-aligned: every event object carries its channel ("progress" | "control" | "monitor").
    /// </summary>
    public string? Channel { get; init; }
    public required string Type { get; init; }
    /// <summary>
    /// TS-aligned: the bookmark is also included inside the event object.
    /// </summary>
    public Bookmark? Bookmark { get; init; }
}

/// <summary>
/// Forward-compatible event wrapper for unknown event types.
/// </summary>
public record UnknownEvent : AgentEvent
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Base class for progress events (UI streaming).
/// </summary>
public abstract record ProgressEvent : AgentEvent;

/// <summary>
/// Base class for control events (approval flow).
/// </summary>
public abstract record ControlEvent : AgentEvent;

/// <summary>
/// Base class for monitor events (observability).
/// </summary>
public abstract record MonitorEvent : AgentEvent;

#region Progress Events

/// <summary>
/// Text chunk streamed from the model.
/// </summary>
public record TextChunkEvent : ProgressEvent
{
    public required int Step { get; init; }
    public required string Delta { get; init; }
}

/// <summary>
/// Text chunk start marker (aligned with TS text_chunk_start).
/// </summary>
public record TextChunkStartEvent : ProgressEvent
{
    public required int Step { get; init; }
}

/// <summary>
/// Text chunk end marker (aligned with TS text_chunk_end).
/// </summary>
public record TextChunkEndEvent : ProgressEvent
{
    public required int Step { get; init; }
    public required string Text { get; init; }
}

/// <summary>
/// Thinking/reasoning chunk (if supported by model).
/// </summary>
public record ThinkChunkEvent : ProgressEvent
{
    public required int Step { get; init; }
    public required string Delta { get; init; }
}

/// <summary>
/// Thinking chunk start marker (aligned with TS think_chunk_start).
/// </summary>
public record ThinkChunkStartEvent : ProgressEvent
{
    public required int Step { get; init; }
}

/// <summary>
/// Thinking chunk end marker (aligned with TS think_chunk_end).
/// </summary>
public record ThinkChunkEndEvent : ProgressEvent
{
    public required int Step { get; init; }
}

/// <summary>
/// Tool execution started.
/// </summary>
public record ToolStartEvent : ProgressEvent
{
    public required ToolCallSnapshot Call { get; init; }
}

/// <summary>
/// Tool execution completed.
/// </summary>
public record ToolEndEvent : ProgressEvent
{
    public required ToolCallSnapshot Call { get; init; }
}

/// <summary>
/// Tool execution error (aligned with TS tool:error).
/// </summary>
public record ToolErrorEvent : ProgressEvent
{
    public required ToolCallSnapshot Call { get; init; }
    public required string Error { get; init; }
}

/// <summary>
/// Agent run completed.
/// </summary>
public record DoneEvent : ProgressEvent
{
    public required int Step { get; init; }
    public required string Reason { get; init; }
}

#endregion

#region Control Events

/// <summary>
/// Permission required for a tool call.
/// </summary>
public record PermissionRequiredEvent : ControlEvent
{
    public required ToolCallSnapshot Call { get; init; }

    /// <summary>
    /// TS-aligned: respond(decision, { note? }) callback (not persisted / not serialized).
    /// </summary>
    [JsonIgnore]
    public Func<string, PermissionRespondOptions?, Task>? Respond { get; init; }
}

public record PermissionRespondOptions
{
    public string? Note { get; init; }
}

/// <summary>
/// Permission decision made.
/// </summary>
public record PermissionDecidedEvent : ControlEvent
{
    public required string CallId { get; init; }
    public required string Decision { get; init; } // allow | deny
    public required string DecidedBy { get; init; }
    public string? Note { get; init; }
}

#endregion

#region Monitor Events

/// <summary>
/// Agent state changed.
/// </summary>
public record StateChangedEvent : MonitorEvent
{
    public required AgentRuntimeState State { get; init; }
}

/// <summary>
/// Breakpoint state changed.
/// </summary>
public record BreakpointChangedEvent : MonitorEvent
{
    public required BreakpointState Previous { get; init; }
    public required BreakpointState Current { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Token usage recorded.
/// </summary>
public record TokenUsageEvent : MonitorEvent
{
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    /// <summary>
    /// TS-aligned: totalTokens = inputTokens + outputTokens.
    /// </summary>
    public required int TotalTokens { get; init; }
}

/// <summary>
/// Error occurred.
/// </summary>
public record ErrorEvent : MonitorEvent
{
    public required string Severity { get; init; } // info | warn | error
    public required string Phase { get; init; } // model | tool | system | lifecycle
    public required string Message { get; init; }
    public object? Detail { get; init; }
}

/// <summary>
/// Model provider is retrying after a transient error (rate limit, overload, timeout).
/// Emitted before each retry delay so consumers can notify users or send typing indicators.
/// </summary>
public record ModelRetryingEvent : MonitorEvent
{
    /// <summary>Provider name, e.g. "anthropic" or "openai".</summary>
    public required string Provider { get; init; }

    /// <summary>1-based retry attempt number.</summary>
    public required int Attempt { get; init; }

    /// <summary>Maximum retries configured for this policy.</summary>
    public required int MaxRetries { get; init; }

    /// <summary>Back-off delay (seconds) before the next attempt.</summary>
    public required double DelaySeconds { get; init; }

    /// <summary>Truncated error message that triggered the retry.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Event store persistence failure (aligned with TS monitor storage_failure; degraded, may be in-memory only).
/// </summary>
public record StorageFailureEvent : MonitorEvent
{
    public required string Severity { get; init; }
    public required string FailedEvent { get; init; }
    public required int BufferedCount { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Context repair telemetry (aligned with TS monitor context_repair).
/// </summary>
public record ContextRepairEvent : MonitorEvent
{
    public required string Reason { get; init; }
    public required int Converted { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// Context compression event (aligned with TS monitor context_compression).
/// </summary>
public record ContextCompressionEvent : MonitorEvent
{
    /// <summary>
    /// Phase: "start" | "end".
    /// </summary>
    public required string Phase { get; init; }

    /// <summary>
    /// Summary text (present on end).
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Compression ratio (present on end).
    /// </summary>
    public double? Ratio { get; init; }
}

/// <summary>
/// Scheduler triggered event (aligned with TS monitor scheduler_triggered).
/// </summary>
public record SchedulerTriggeredEvent : MonitorEvent
{
    public required string TaskId { get; init; }
    public required string Spec { get; init; }
    /// <summary>
    /// Kind: steps|time|cron.
    /// </summary>
    public required string Kind { get; init; }
    public required long TriggeredAt { get; init; }
}

/// <summary>
/// Agent recovered from an inconsistent persisted state (aligned with TS monitor agent_recovered).
/// </summary>
public record AgentRecoveredEvent : MonitorEvent
{
    public required string Reason { get; init; }
    public object? Detail { get; init; }
}

/// <summary>
/// Tool executed successfully (aligned with TS monitor tool_executed).
/// </summary>
public record ToolExecutedEvent : MonitorEvent
{
    public required ToolCallSnapshot Call { get; init; }
}

/// <summary>
/// Agent resumed from checkpoint.
/// </summary>
public record AgentResumedEvent : MonitorEvent
{
    public required string Strategy { get; init; } // crash | manual
    public required IReadOnlyList<ToolCallSnapshot> Sealed { get; init; }
}

/// <summary>
/// Step completion marker (aligned with TS monitor step_complete).
/// </summary>
public record StepCompleteEvent : MonitorEvent
{
    public required int Step { get; init; }
    public long? DurationMs { get; init; }
    // public Bookmark? Bookmark { get; init; }
}

/// <summary>
/// Reminder sent (aligned with TS monitor reminder_sent).
/// </summary>
public record ReminderSentEvent : MonitorEvent
{
    /// <summary>
    /// Category: file|todo|security|performance|general.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Reminder content (already wrapped, if applicable).
    /// </summary>
    public required string Content { get; init; }
}

/// <summary>
/// Skills discovered (aligned with TS monitor skill_discovered).
/// </summary>
public record SkillDiscoveredEvent : MonitorEvent
{
    public required IReadOnlyList<string> Skills { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Skill activated (aligned with TS monitor skill_activated).
/// </summary>
public record SkillActivatedEvent : MonitorEvent
{
    /// <summary>
    /// Single skill name or comma-separated list (TS semantics).
    /// </summary>
    public required string Skill { get; init; }

    /// <summary>
    /// auto|agent|user.
    /// </summary>
    public required string ActivatedBy { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Skill deactivated (aligned with TS monitor skill_deactivated).
/// </summary>
public record SkillDeactivatedEvent : MonitorEvent
{
    public required string Name { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Sub-agent created (aligned with TS monitor subagent.created).
/// </summary>
public record SubAgentCreatedEvent : MonitorEvent
{
    public string? CallId { get; init; }
    public required string AgentId { get; init; }
    public required string TemplateId { get; init; }
    public required string ParentAgentId { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Sub-agent streamed text delta (aligned with TS monitor subagent.delta).
/// </summary>
public record SubAgentDeltaEvent : MonitorEvent
{
    public required string SubAgentId { get; init; }
    public required string TemplateId { get; init; }
    public string? CallId { get; init; }
    public required string Delta { get; init; }
    public required string Text { get; init; }
    public int? Step { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Sub-agent streamed thinking delta (aligned with TS monitor subagent.thinking).
/// </summary>
public record SubAgentThinkingEvent : MonitorEvent
{
    public required string SubAgentId { get; init; }
    public required string TemplateId { get; init; }
    public string? CallId { get; init; }
    public required string Delta { get; init; }
    public int? Step { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Sub-agent tool start (aligned with TS monitor subagent.tool_start).
/// </summary>
public record SubAgentToolStartEvent : MonitorEvent
{
    public required string SubAgentId { get; init; }
    public required string TemplateId { get; init; }
    public string? ParentCallId { get; init; }
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public object? InputPreview { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Sub-agent tool end (aligned with TS monitor subagent.tool_end).
/// </summary>
public record SubAgentToolEndEvent : MonitorEvent
{
    public required string SubAgentId { get; init; }
    public required string TemplateId { get; init; }
    public string? ParentCallId { get; init; }
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public long? DurationMs { get; init; }
    public bool IsError { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Sub-agent permission required (aligned with TS monitor subagent.permission_required).
/// </summary>
public record SubAgentPermissionRequiredEvent : MonitorEvent
{
    public required string SubAgentId { get; init; }
    public required string TemplateId { get; init; }
    public string? ParentCallId { get; init; }
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Todo list changed (aligned with TS monitor todo_changed).
/// </summary>
public record TodoChangedEvent : MonitorEvent
{
    public required IReadOnlyList<TodoItem> Current { get; init; }
    public required IReadOnlyList<TodoItem> Previous { get; init; }
}

/// <summary>
/// Todo reminder emitted (aligned with TS monitor todo_reminder).
/// </summary>
public record TodoReminderEvent : MonitorEvent
{
    public required IReadOnlyList<TodoItem> Todos { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// External file change detected (aligned with TS monitor file_changed).
/// </summary>
public record FileChangedEvent : MonitorEvent
{
    public required string Path { get; init; }
    public required long Mtime { get; init; }
}

/// <summary>
/// Tool manual updated and injected into system prompt (aligned with TS monitor tool_manual_updated).
/// </summary>
public record ToolManualUpdatedEvent : MonitorEvent
{
    public required IReadOnlyList<string> Tools { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Tool custom event for observability (aligned with TS monitor tool_custom_event).
/// </summary>
public record ToolCustomEvent : MonitorEvent
{
    public required string ToolName { get; init; }
    public required string EventType { get; init; }
    public object? Data { get; init; }
    public required long Timestamp { get; init; }
}

#endregion
