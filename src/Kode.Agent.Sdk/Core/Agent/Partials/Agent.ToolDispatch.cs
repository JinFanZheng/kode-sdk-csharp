using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Hooks;
using Kode.Agent.Sdk.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Kode.Agent.Sdk.Core.Agent;

// Per-step tool dispatch. ProcessToolCallsAsync walks each tool_use block from the
// model, applies the full gating pipeline (pre-tool hook, enabled-tool check,
// schema validation with streak-based recovery nudges, deny/approval permission
// manager, timeout-bounded ToolRunner.ExecuteAsync, post-tool hook, oversize
// compression) and streams tool:start / tool:executed / tool:error / tool:end
// events. tool:end emission is moved into an outer try/finally so it is
// symmetric with tool:start even when the loop exits via OperationCanceledException
// (e.g. outer cancel during pre-hook or approval wait). ClassifyToolCategory tags
// tool metrics by coarse category; ComputeContextPressure exposes the current
// usage/threshold ratio to tools; GetSnapshotOrFallback guarantees a
// ToolCallSnapshot even if ToolRunner has no entry yet (e.g. a deny path before
// execute).
public sealed partial class Agent
{
    private async Task<List<(string CallId, ToolResult Result)>> ProcessToolCallsAsync(
        List<ToolUseContent> toolUses,
        CancellationToken cancellationToken)
    {
        var results = new List<(string CallId, ToolResult Result)>();

        // Compute context pressure once per tool batch. _messages stays constant across
        // the batch (Step.cs appends tool results only after this method returns), so
        // the same pressure value is valid for every iteration and the post-tool
        // result compressor.
        var contextPressure = ComputeContextPressure();

        foreach (var toolUse in toolUses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var callId = toolUse.Id;
            var toolName = toolUse.Name;
            // Serialize input once — reused for hookCall and outcome.
            var inputElement = System.Text.Json.JsonSerializer.SerializeToElement(toolUse.Input);

            _toolRunner.RegisterToolCall(callId, toolName, toolUse.Input);
            var startSnap = _toolRunner.GetSnapshot(callId) ?? new ToolCallSnapshot
            {
                Id = callId,
                Name = toolName,
                State = ToolCallState.Pending,
                Approval = new ToolCallApproval { Required = false }
            };
            _eventBus.EmitProgress(new ToolStartEvent
            {
                Type = "tool:start",
                Call = startSnap
            });

            // Outer try/finally guarantees tool:end is emitted for every tool:start, even
            // when the iteration exits via exception (outer cancel through pre-hook or
            // approval await). All inline tool:end emissions are removed in favour of
            // this single exit point.
            try
            {
                var hookCall = new ToolCall(callId, toolName, inputElement);

                var context = new ToolContext
                {
                    AgentId = AgentId,
                    CallId = callId,
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
                            ToolName = toolName,
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
                    _toolRunner.DenyToolCall(callId, deny.Reason);
                    results.Add((callId, ToolResult.Fail(deny.Reason)));
                    continue;
                }

                if (preDecision is SkipDecision skip)
                {
                    var mock = ToolResult.Ok(skip.MockResult);
                    _toolRunner.UpdateFinalResult(callId, mock);
                    var mockSnapshot = _toolRunner.GetSnapshot(callId);
                    if (mockSnapshot != null && mock.Success)
                    {
                        _eventBus.EmitMonitor(new ToolExecutedEvent
                        {
                            Type = "tool_executed",
                            Call = mockSnapshot
                        });
                    }
                    results.Add((callId, mock));
                    continue;
                }

                // Tool must be enabled/exposed for this agent run (align with TS tool selection semantics).
                // Single-pass lookup replaces prior `_tools.All(...)` + `_tools.FirstOrDefault(...)` double pass.
                var toolInstance = _tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
                if (toolInstance == null)
                {
                    const string message = "Tool is not enabled for this agent";
                    _toolRunner.DenyToolCall(callId, message);
                    results.Add((callId, ToolResult.Fail(message)));
                    continue;
                }

                // Tool input validation (best-effort; aligns with TS schema validation behavior).
                var validation = ToolInputValidator.Validate(toolInstance.InputSchema, toolUse.Input);
                if (!validation.Ok)
                {
                    if (string.Equals(_invalidToolArgsLastTool, toolName, StringComparison.OrdinalIgnoreCase))
                    {
                        _invalidToolArgsStreak += 1;
                    }
                    else
                    {
                        _invalidToolArgsLastTool = toolName;
                        _invalidToolArgsStreak = 1;
                    }

                    if (_invalidToolArgsStreak >= 2)
                    {
                        _nextModelToolsOverride = new NextModelToolsOverride(
                            NextModelToolsMode.Allowlist,
                            [toolName],
                            "recover_invalid_tool_args");
                    }

                    if (_invalidToolArgsStreak >= 3)
                    {
                        var requiredKeys = validation.RequiredKeys is { Count: > 0 }
                            ? string.Join(", ", validation.RequiredKeys)
                            : "(see tool schema)";
                        _nextModelNudgeText =
                            $"Your last tool call to `{toolName}` failed schema validation ({validation.Error}). " +
                            $"Retry by emitting ONLY one `tool_use` for `{toolName}` with a complete JSON object. " +
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

                    var failMessage = $"Tool input validation failed for {toolName}: {validation.Error ?? "invalid input"}";
                    var fail = ToolResult.Fail(failMessage);
                    _toolRunner.UpdateFinalResult(callId, fail);
                    _eventBus.EmitProgress(new ToolErrorEvent
                    {
                        Type = "tool:error",
                        Call = GetSnapshotOrFallback(callId, toolName),
                        Error = failMessage
                    });
                    results.Add((callId, fail));
                    continue;
                }

                // Any successful validation resets the streak.
                _invalidToolArgsStreak = 0;
                _invalidToolArgsLastTool = "";

                // Hard deny: allowlist / deny list
                if (_permissionManager.IsDenied(toolName, out var denyReason))
                {
                    _toolRunner.DenyToolCall(callId, denyReason);
                    results.Add((callId, ToolResult.Fail(denyReason)));
                    continue;
                }

                // Check if approval is required
                var forceApproval = preDecision is RequireApprovalDecision;
                if (forceApproval || _permissionManager.RequiresApproval(toolName, toolUse.Input))
                {
                    _breakpointManager.TransitionTo(BreakpointState.AwaitingApproval);
                    TransitionState(AgentRuntimeState.Paused);

                    // OT-1C: span measuring how long approval takes (human-in-the-loop wait time).
                    bool approved;
                    using (var approvalActivity = KodeAgentActivitySource.Source.StartActivity("agent.tool.approval_wait"))
                    {
                        approvalActivity?.SetTag("tool.name", toolName);
                        approvalActivity?.SetTag("tool.id", callId);
                        approved = await _permissionManager.RequestApprovalAsync(
                            callId,
                            toolName,
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
                        _toolRunner.DenyToolCall(callId, message);
                        results.Add((callId, ToolResult.Fail(message)));
                        continue;
                    }
                }

                _breakpointManager.TransitionTo(BreakpointState.PreTool);
                _breakpointManager.TransitionTo(BreakpointState.ToolExecuting);

                using var toolActivity = KodeAgentActivitySource.Source.StartActivity("agent.tool.execute");
                toolActivity?.SetTag("tool.name", toolName);
                toolActivity?.SetTag("tool.id", callId);

                TouchProcessingHeartbeat();
                var sw = Stopwatch.StartNew();
                ToolResult toolResult;

                // Cancellation wiring: arm the timeout BEFORE publishing the CTS to
                // _activeToolCalls so any external interrupt sees a fully-wired source.
                // TryAdd defends against duplicate tool_use ids without silently
                // orphaning a previous CTS (the rare collision is logged instead).
                var toolCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var registered = false;
                try
                {
                    if (_config.ToolTimeout > TimeSpan.Zero)
                    {
                        toolCts.CancelAfter(_config.ToolTimeout);
                    }

                    lock (_activeToolCallsLock)
                    {
                        registered = _activeToolCalls.TryAdd(callId, toolCts);
                    }
                    if (!registered)
                    {
                        _logger?.LogWarning(
                            "Duplicate tool call id {CallId} during dispatch — first registration retained; new CTS remains local and will not be reachable via Interrupt.",
                            callId);
                    }

                    var execContext = context with { CancellationToken = toolCts.Token };
                    toolResult = await _toolRunner.ExecuteAsync(callId, toolName, toolUse.Input, execContext, toolCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Inner CTS cancelled (timeout or explicit interrupt) but outer token is still live.
                    toolResult = ToolResult.Fail("Tool timed out or cancelled");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    toolResult = ToolResult.Fail(ex.Message);
                }
                finally
                {
                    if (registered)
                    {
                        lock (_activeToolCallsLock)
                        {
                            _activeToolCalls.Remove(callId);
                        }
                    }
                    toolCts.Dispose();
                }
                sw.Stop();
                TouchProcessingHeartbeat();

                // OT-2E: tag tool metrics with category dimension for cost breakdown by tool type.
                var toolCategory = ClassifyToolCategory(toolName);
                var toolDimensions = new TagList
                {
                    { "tool.name", toolName },
                    { "tool_category", toolCategory }
                };
                KodeAgentMetrics.ToolExecutions.Add(1, toolDimensions);
                KodeAgentMetrics.ToolDuration.Record(sw.Elapsed.TotalMilliseconds, toolDimensions);
                toolActivity?.SetTag("tool.duration_ms", sw.Elapsed.TotalMilliseconds);
                toolActivity?.SetTag("tool.category", toolCategory);

                // OT-3B: desensitized structured log — no tool args/output, only metadata.
                if (_logger?.IsEnabled(LogLevel.Debug) == true)
                {
                    _logger.LogDebug(
                        "Tool executed: {ToolName} category={ToolCategory} success={Success} duration_ms={DurationMs}",
                        toolName, toolCategory, toolResult.Success, sw.ElapsedMilliseconds);
                }

                var outcome = new ToolOutcome(
                    callId,
                    toolName,
                    inputElement,
                    toolResult,
                    !toolResult.Success,
                    sw.Elapsed);

                var postOutcome = await _hookManager.RunPostToolUseAsync(outcome, context, cancellationToken);

                // Compress oversized tool results before they enter the message history.
                // Reuse the batch-level contextPressure — _messages is unchanged within this loop.
                var finalResult = postOutcome.Result;
                if (_toolResultCompressor != null && _config.Context?.ToolResultCompression != null)
                {
                    finalResult = await _toolResultCompressor.CompressIfNeededAsync(
                        toolName,
                        finalResult,
                        _messages,
                        _config.Context.ToolResultCompression,
                        contextPressure: contextPressure,
                        cancellationToken: cancellationToken);
                }

                _toolRunner.UpdateFinalResult(callId, finalResult);
                var snapAfter = _toolRunner.GetSnapshot(callId);
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
                        Call = GetSnapshotOrFallback(callId, toolName),
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

                results.Add((callId, finalResult));
            }
            finally
            {
                // Symmetric tool:end emission — fires for every tool:start regardless of exit path.
                _eventBus.EmitProgress(new ToolEndEvent
                {
                    Type = "tool:end",
                    Call = GetSnapshotOrFallback(callId, toolName)
                });
            }
        }

        return results;
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
}
