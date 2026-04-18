using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Events;
using Kode.Agent.Sdk.Diagnostics;

namespace Kode.Agent.Sdk.Core.Agent;

// The single model-call + tool-execution turn. StepAsync is the core loop body
// called from both RunAsync (synchronous TS-aligned path) and EnsureProcessing's
// background Task.Run (async queued path). It flushes pending messages, runs
// defensive sanitization, triggers context compression, calls the model via
// StreamModelResponseAsync, retries on empty/overflow, dispatches tool_use
// through ProcessToolCallsAsync, and emits the DoneEvent + StepCompleteEvent
// envelope. Two Step-scoped types live here: NextModelToolsMode /
// NextModelToolsOverride — used by BuildModelRequest to tether the next model
// call to a narrower tool set (e.g. after loop detection).
public sealed partial class Agent
{
    private enum NextModelToolsMode
    {
        None,
        Allowlist
    }

    private sealed record NextModelToolsOverride(
        NextModelToolsMode Mode,
        IReadOnlyList<string>? Allow,
        string Reason);

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

        // Force-compress + retry trigger. Two paths converge here:
        //  (1) Empty content with no tools/text/thinking — some models (e.g. GLM-5-Turbo)
        //      return empty on context overflow rather than an error code.
        //  (2) Explicit ContextOverflow stop_reason — the AnthropicProvider surfaced a
        //      non-standard overflow signal (e.g. "model_context_window_exceeded") that the
        //      SDK enum would have otherwise masked as EndTurn.
        if (response.Content.Count == 0 || response.StopReason == ModelStopReason.ContextOverflow)
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
}
