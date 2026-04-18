using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Events;
using Kode.Agent.Sdk.Core.Hooks;
using Kode.Agent.Sdk.Core.Scheduling;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.Sdk.Core.Agent;

// Construction / bootstrap / teardown. Owns the private ctor (wires EventBus,
// BreakpointManager, Scheduler, MessageQueue, HookManager, ContextManager,
// ToolRunner, PermissionManager), the CreateAsync factory (sandbox + tool
// services + todo + skills + tool-manual + initial UpdateInfoAsync), DisposeAsync
// (cancels CTS, disposes subsystems in the documented order), and the helpers
// LoadTools + RegisterHooks that the ctor calls directly.
public sealed partial class Agent
{
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

        // Host-injected compressor wins when ToolResultCompression is configured (any Enabled value);
        // otherwise fall back to the built-in LLM summariser when Enabled=true.
        _toolResultCompressor = _config.Context?.ToolResultCompression switch
        {
            null => null,
            _ when dependencies.ToolResultCompressor is not null => dependencies.ToolResultCompressor,
            { Enabled: true } when dependencies.ModelProvider != null => new LlmToolResultCompressor(
                dependencies.ModelProvider,
                _config.Model,
                dependencies.LoggerFactory?.CreateLogger<LlmToolResultCompressor>()),
            _ => null
        };

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
        // Tear down any outstanding On() subscriptions whose callers never disposed them.
        List<CancellationTokenSource> pendingOnSubs;
        lock (_onSubscriptionsLock)
        {
            pendingOnSubs = [.. _onSubscriptions];
            _onSubscriptions.Clear();
        }
        foreach (var cts in pendingOnSubs)
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
}
