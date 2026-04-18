using Kode.Agent.Sdk.Core.Events;
using System.Text.Json;

namespace Kode.Agent.Sdk.Core.Agent;

// Sub-agent / task delegation surface:
//   - public DelegateTaskAsync (task_tool path)
//   - public SpawnSubAgentAsync (subagent config path)
//   - private ForwardSubAgentEventsAsync (re-emits child events on parent bus)
//   - private BuildInputPreview (event payload helper)
public sealed partial class Agent
{
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
}
