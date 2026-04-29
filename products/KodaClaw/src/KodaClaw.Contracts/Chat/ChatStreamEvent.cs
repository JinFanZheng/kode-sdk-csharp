using KodaClaw.Contracts.System;

namespace KodaClaw.Contracts.Chat;

public sealed record ChatStreamEvent(
    string Type,
    string SessionId,
    int? Step = null,
    long? Sequence = null,
    long? Timestamp = null,
    string? Delta = null,
    string? Reason = null,
    ErrorResponse? Error = null,
    // Approval events (approval_required / approval_decided)
    string? ApprovalId = null,
    string? CallId = null,
    string? ToolName = null,
    string? InputPreview = null,
    string? Decision = null,
    // Tool activity events (tool_activity / agent_working)
    long? DurationMs = null,
    // Sub-agent progress events (subagent_start / subagent_working / subagent_tool_done)
    string? SubAgentId = null,
    string? Label = null,
    string? SubAgentToolName = null,
    // Thinking stream (think_chunk_start / think_chunk / think_chunk_end)
    string? ThinkingDelta = null);
