using System.Net.Http;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Bootstrap;
using KodaClaw.Contracts.Canvas;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Chat;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Jobs;
using KodaClaw.Contracts.Models;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Timers;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime.Bootstrap;
using KodaClaw.Runtime.Diagnostics;
using KodaClaw.Runtime.Providers;
using KodaClaw.Runtime.Sessions;
using KodaClaw.Runtime.Tools;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Extensions;
using Kode.Agent.Sdk.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Kode.Agent.Tools.Builtin;
using Kode.Agent.Tools.Orchestration;

namespace KodaClaw.Runtime;

public sealed class KodaClawRuntimeOptions
{
    public string? DefaultModel { get; set; }

    public string? OpenAIApiKey { get; set; }

    public string? OpenAIBaseUrl { get; set; }

    public string? AnthropicApiKey { get; set; }

    public string? AnthropicBaseUrl { get; set; }

    public string? DeepSeekApiKey { get; set; }

    public string? DeepSeekBaseUrl { get; set; }

    /// <summary>
    /// Optional per-model capability overrides. Entries here override the SDK's
    /// built-in registry, so new or custom models can be configured without an SDK
    /// upgrade. Key is the model ID (case-insensitive).
    /// </summary>
    public IReadOnlyDictionary<string, ModelCapabilities>? ModelCapabilitiesOverride { get; set; }

    public string? SystemPrompt { get; set; }

    public int MainMaxIterations { get; set; } = 30;

    public int ChannelMaxIterations { get; set; } = 15;

    public int AutomationMaxIterations { get; set; } = 50;

    public double ContextCompressionTriggerRatio { get; set; } = 0.75;

    public double ContextCompressionTargetRatio { get; set; } = 0.50;

    public int DefaultContextWindowSize { get; set; } = 128_000;
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKodaClawRuntime(
        this IServiceCollection services,
        Action<KodaClawRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new KodaClawRuntimeOptions();
        configure?.Invoke(options);
        var snapshot = RuntimeConfigurationSnapshot.FromOptions(options);

        services.TryAddSingleton<IRuntimeConfigurationResolver>(
            new StaticRuntimeConfigurationResolver(snapshot));
        services.AddHttpClient(nameof(OpenAIProvider))
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        services.AddHttpClient(nameof(OpenAIResponsesProvider))
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        services.AddHttpClient(nameof(DeepSeekProvider))
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        var anthropicBuilder = services.AddHttpClient(nameof(AnthropicProvider))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler())
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        var httpDumpDir = Environment.GetEnvironmentVariable("KODACLAW_HTTP_DUMP_DIR");
        if (!string.IsNullOrWhiteSpace(httpDumpDir))
        {
            var keepAll = string.Equals(
                Environment.GetEnvironmentVariable("KODACLAW_HTTP_DUMP_MODE"),
                "all",
                StringComparison.OrdinalIgnoreCase);
            anthropicBuilder.AddHttpMessageHandler(() => new HttpDumpHandler(httpDumpDir, keepAll));
        }
        services.TryAddSingleton<IRuntimeModelProviderFactory, DefaultRuntimeModelProviderFactory>();
        services.TryAddSingleton<DynamicModelProvider>();
        services.TryAddSingleton<IModelProvider, AccountAwareModelProvider>();

        services.AddAgentSdk();

        services.TryAddSingleton(new MainSessionOptions
        {
            Model = options.DefaultModel ?? string.Empty,
            SystemPrompt = options.SystemPrompt ?? "You are KodaClaw main assistant.",
            ContextCompressionTriggerRatio = options.ContextCompressionTriggerRatio,
            ContextCompressionTargetRatio = options.ContextCompressionTargetRatio,
            DefaultContextWindowSize = options.DefaultContextWindowSize,
            MaxIterations = options.MainMaxIterations,
        });
        services.TryAddSingleton(new BootstrapDraftOptions
        {
            Model = options.DefaultModel ?? string.Empty,
            SystemPrompt = options.SystemPrompt ?? "You are KodaClaw bootstrap assistant.",
        });
        services.TryAddSingleton(new AutomationSessionOptions
        {
            Model = options.DefaultModel ?? string.Empty,
            SystemPrompt = options.SystemPrompt ?? "You are KodaClaw automation assistant.",
            MaxIterations = options.AutomationMaxIterations,
            ContextCompressionTriggerRatio = options.ContextCompressionTriggerRatio,
            ContextCompressionTargetRatio = options.ContextCompressionTargetRatio,
            DefaultContextWindowSize = options.DefaultContextWindowSize,
        });
        services.TryAddSingleton(new ChannelSessionOptions
        {
            Model = options.DefaultModel ?? string.Empty,
            SystemPrompt = options.SystemPrompt ?? "You are KodaClaw channel assistant.",
            MaxIterations = options.ChannelMaxIterations,
            ContextCompressionTriggerRatio = options.ContextCompressionTriggerRatio,
            ContextCompressionTargetRatio = options.ContextCompressionTargetRatio,
            DefaultContextWindowSize = options.DefaultContextWindowSize,
        });
        services.TryAddSingleton<IMainSessionAgentDependenciesFactory>(sp =>
        {
            var toolRegistry = sp.GetRequiredService<IToolRegistry>();
            toolRegistry.RegisterBuiltinTools();

            var workspaceService = sp.GetRequiredService<IWorkspaceService>();
            toolRegistry.Register("get_current_datetime",
                _ => new GetCurrentDateTimeTool());

            var correlationContextAccessor = sp.GetService<ICorrelationContextAccessor>();
            var diagnosticsService = sp.GetService<IDiagnosticsService>();

            toolRegistry.Register("workspace_memory_append",
                _ => new WorkspaceMemoryAppendTool(workspaceService, diagnosticsService));
            toolRegistry.Register("workspace_protocol_update",
                _ => new WorkspaceProtocolUpdateTool(workspaceService, diagnosticsService));

            var canvasRepository = sp.GetRequiredService<ICanvasArtifactRepository>();
            toolRegistry.Register("canvas_upsert",
                _ => new CanvasUpsertTool(workspaceService, canvasRepository, correlationContextAccessor, diagnosticsService));

            var inboxRepository = sp.GetRequiredService<IInboxRepository>();
            toolRegistry.Register("inbox_create",
                _ => new InboxCreateTool(inboxRepository, correlationContextAccessor, diagnosticsService));
            toolRegistry.Register("inbox_read",
                _ => new InboxReadTool(inboxRepository));
            var bindingRepository = sp.GetService<IThreadBindingRepository>();
            toolRegistry.Register("workspace_read",
                _ => new WorkspaceReadTool(workspaceService, bindingRepository));

            var accountRepository = sp.GetService<IProviderAccountRepository>();
            var secretStore = sp.GetService<ISecretStore>();
            if (accountRepository is not null && secretStore is not null)
            {
                toolRegistry.Register("config_update",
                    _ => new ConfigUpdateTool(accountRepository, secretStore, diagnosticsService));
            }

            var channelSendService = sp.GetService<IChannelSendService>();
            if (channelSendService is not null)
            {
                toolRegistry.Register("channel_send",
                    _ => new ChannelSendTool(channelSendService));
            }

            if (bindingRepository is not null)
            {
                toolRegistry.Register("channel_list",
                    _ => new ChannelListTool(bindingRepository));
            }

            if (diagnosticsService is not null)
            {
                toolRegistry.Register("diagnostics_query",
                    _ => new DiagnosticsQueryTool(diagnosticsService, correlationContextAccessor));
            }

            var oneShotTimerRepository = sp.GetService<IOneShotTimerRepository>();
            if (oneShotTimerRepository is not null)
            {
                toolRegistry.Register("schedule_reminder",
                    _ => new ScheduleReminderTool(oneShotTimerRepository, diagnosticsService));
            }

            var jobRepository = sp.GetService<IJobRepository>();
            if (jobRepository is not null)
            {
                toolRegistry.Register("job_create", _ => new JobCreateTool(jobRepository));
                toolRegistry.Register("job_list", _ => new JobListTool(jobRepository));
                toolRegistry.Register("job_status", _ => new JobStatusTool(jobRepository));
                toolRegistry.Register("job_result", _ => new JobResultTool(jobRepository));
                toolRegistry.Register("job_update", _ => new JobUpdateTool(jobRepository));
                toolRegistry.Register("job_cancel", _ => new JobCancelTool(jobRepository));
                toolRegistry.Register("job_delete", _ => new JobDeleteTool(jobRepository));
            }

            var modelProvider = sp.GetRequiredService<IModelProvider>();
            var sandboxFactory = sp.GetService<ISandboxFactory>();
            var loggerFactory = sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>();
            if (sandboxFactory is not null)
            {
                toolRegistry.RegisterOrchestrationTools(
                    modelProvider,
                    options.DefaultModel ?? string.Empty,
                    sandboxFactory,
                    loggerFactory,
                    skillsPaths: workspaceService.GetSkillsPaths());
            }

            return new DefaultMainSessionAgentDependenciesFactory(new MainSessionDependencies
            {
                ModelProvider = modelProvider,
                ToolRegistry = toolRegistry,
                SandboxFactory = sandboxFactory,
                LoggerFactory = loggerFactory,
                WorkspaceRootPath = workspaceService.RootPath,
                DiagnosticsService = diagnosticsService,
            });
        });
        services.TryAddSingleton<IMemorySessionSummaryService, MemorySessionSummaryService>();
        services.TryAddSingleton<IMemoryConsolidationService, MemoryConsolidationService>();
        services.TryAddSingleton<IMainSessionService, MainSessionService>();
        services.TryAddSingleton<IBootstrapDraftService, BootstrapDraftService>();
        services.TryAddSingleton<IAutomationSessionService, AutomationSessionService>();
        services.TryAddSingleton<IChannelSessionStatsTracker, ChannelSessionStatsTracker>();
        services.TryAddSingleton<IChannelSessionService, ChannelSessionService>();
        services.TryAddSingleton<IChatSessionService, ChatSessionService>();
        return services;
    }
}
