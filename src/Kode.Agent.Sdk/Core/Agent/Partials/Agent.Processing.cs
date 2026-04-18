using Kode.Agent.Sdk.Core.Events;
using Kode.Agent.Sdk.Diagnostics;
using System.Diagnostics;

namespace Kode.Agent.Sdk.Core.Agent;

// Background processing-loop plumbing. TransitionState emits state_changed and
// maintains turn timing invariants (_turnStartedAtMs / _lastActivityAtMs).
// EnsureProcessing owns the Task.Run loop that drives StepAsync under the
// ProcessingTimeout watchdog, with runId staleness checks so a force-restarted
// stale task can't race a newly-started one. Called from user-message enqueue
// paths (RunAsync / Send) after EnqueueMessageAsync.
public sealed partial class Agent
{
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
            // Also reset loop-detection fingerprints so the dictionary doesn't grow unbounded
            // across long-running sessions, and a fresh user turn doesn't trip the nudge from
            // a prior run's repeated calls.
            _toolCallFingerprints.Clear();
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
}
