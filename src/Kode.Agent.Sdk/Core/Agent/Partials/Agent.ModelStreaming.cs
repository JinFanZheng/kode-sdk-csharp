using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Events;
using Kode.Agent.Sdk.Diagnostics;
using Kode.Agent.Sdk.Infrastructure.Providers;
using System.Diagnostics;

namespace Kode.Agent.Sdk.Core.Agent;

// Model provider plumbing. BuildModelRequest constructs the per-step request
// (applying any NextModelToolsOverride gating from Agent.Step); StreamModelResponseAsync
// drains the provider stream into ContentBlocks while emitting Progress events for
// every text/think chunk and tool_use fragment, and includes a small in-place retry
// loop (3 attempts, only before any content has been surfaced) for transient
// provider errors. ClassifyModelError labels those transient errors for OT-2C
// metric dimensions.
public sealed partial class Agent
{
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
        var thinkingSignatureBuilder = new System.Text.StringBuilder();
        var toolUseBuilders = new Dictionary<string, (string Name, System.Text.StringBuilder Input)>();
        TokenUsage? usage = null;
        ModelStopReason stopReason = ModelStopReason.EndTurn;
        var textStarted = false;
        var thinkingStarted = false;

        // Install SDK-level retry hook so ProviderRetryHelper emits ModelRetryingEvent on every
        // back-off delay — all consumers (chat, channel, automation) benefit automatically.
        // Save/restore the previous AsyncLocal value so nested calls (e.g. sub-agent spawned
        // from a tool) don't wipe out the parent's hook on exit.
        var previousRetryHook = AgentRetryContext.Current.Value;
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
                            // ThinkingDelta chunks carry either thinking text OR a signature (not both).
                            // Text is always accumulated regardless of ExposeThinking so it can be
                            // passed back in future turns (required by Anthropic / DeepSeek Anthropic-compat).
                            if (chunk.ThinkingSignature != null)
                            {
                                thinkingSignatureBuilder.Append(chunk.ThinkingSignature);
                            }
                            if (chunk.ThinkingDelta != null)
                            {
                                thinkingBuilder.Append(chunk.ThinkingDelta);
                                // Only surface thinking to the front-end when ExposeThinking is enabled.
                                if ((_currentRunOptions?.ExposeThinking ?? _config.ExposeThinking) == true)
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
                // Jitter ±25% around the base delay so concurrent agents hitting the same
                // provider outage don't synchronize their retries.
                var baseDelayMs = providerAttempt == 1 ? 1000 : 2000;
                var jitterMs = Random.Shared.Next(-baseDelayMs / 4, (baseDelayMs / 4) + 1);
                await Task.Delay(baseDelayMs + jitterMs, cancellationToken);
            }

            if (attemptSucceeded)
            {
                break;
            }
        }

        } // end try (AgentRetryContext)
        finally
        {
            AgentRetryContext.Current.Value = previousRetryHook;
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

        // Add thinking content if any.
        // Always written to history regardless of ExposeThinking — Anthropic and Anthropic-compatible
        // providers (e.g. DeepSeek) require thinking blocks with their signature to be passed back
        // verbatim in any turn that contained tool calls; omitting them causes a 400 error.
        if (thinkingBuilder.Length > 0)
        {
            var signature = thinkingSignatureBuilder.Length > 0 ? thinkingSignatureBuilder.ToString() : null;
            contentBlocks.Insert(0, new ThinkingContent
            {
                Thinking = thinkingBuilder.ToString(),
                Signature = signature
            });
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
                TotalTokens = usage.InputTokens + usage.OutputTokens,
                CacheHitTokens = usage.CacheHitTokens,
                CacheMissTokens = usage.CacheMissTokens
            });
            KodeAgentMetrics.TokensInput.Add(usage.InputTokens, modelDimensions);
            KodeAgentMetrics.TokensOutput.Add(usage.OutputTokens, modelDimensions);
            modelActivity?.SetTag("gen_ai.usage.input_tokens", usage.InputTokens);
            modelActivity?.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);

            // Calibrate the local CJK-aware estimator against what the provider actually billed.
            // Uses the raw (uncalibrated) estimate of the messages we just sent so the EMA in
            // ContextManager can converge to the real ratio.
            var systemPromptTokens = ContextManager.EstimateSystemPromptTokens(request.SystemPrompt);
            var rawEstimate = _contextManager.EstimateMessagesTokensRaw(request.Messages, systemPromptTokens);
            _contextManager.RecordServerUsage(usage.InputTokens, rawEstimate);
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
}
