using System.Collections.Concurrent;
using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.McpHub;
using KodaClaw.PluginHost.Hosting;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Agent;
using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Store.Json;
using AgentRuntime = Kode.Agent.Sdk.Core.Agent.Agent;

namespace KodaClaw.Runtime;

public sealed class MainSessionService : IMainSessionService, IAsyncDisposable
{
    private const string DiagnosticSource = "koda.runtime.main_session";
    private const string ApprovalSource = "runtime.main_session.approval";
    private const string StaleApprovalDecisionBy = "runtime.resume";
    private const int ApprovalDecisionPollAttempts = 100;
    private const int ApprovalDecisionPollDelayMs = 50;
    private const int MaxPluginInjectionCandidates = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IWorkspaceService _workspaceService;
    private readonly IMainSessionAgentDependenciesFactory _dependenciesFactory;
    private readonly MainSessionOptions _options;
    private readonly ConcurrentDictionary<string, IAgent> _agents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SessionControlSubscriptions> _sessionSubscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LiveApprovalContext> _liveApprovals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resumedSessionIds = new(StringComparer.Ordinal);
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly ICorrelationContextAccessor? _correlationContextAccessor;
    private readonly IApprovalRepository? _approvalRepository;
    private readonly IInboxRepository? _inboxRepository;
    private readonly IPluginRegistryRepository? _pluginRegistryRepository;
    private readonly IPluginLifecycleHost? _pluginLifecycleHost;
    private readonly IRuntimeConfigurationResolver? _runtimeConfigurationResolver;
    private readonly IWorkspaceReadinessService? _workspaceReadinessService;
    private readonly KodaClaw.Contracts.IProviderAccountRepository? _accountRepository;
    private readonly IMcpHubService? _mcpHubService;
    private readonly ISettingsRepository? _settingsRepository;
    private readonly IMemorySessionSummaryService? _sessionSummaryService;
    private volatile bool _pendingWorkspaceRotation;

    public MainSessionService(
        IWorkspaceService workspaceService,
        IMainSessionAgentDependenciesFactory dependenciesFactory,
        MainSessionOptions? options = null,
        IDiagnosticsService? diagnosticsService = null,
        ICorrelationContextAccessor? correlationContextAccessor = null,
        IApprovalRepository? approvalRepository = null,
        IInboxRepository? inboxRepository = null,
        IPluginRegistryRepository? pluginRegistryRepository = null,
        IPluginLifecycleHost? pluginLifecycleHost = null,
        IRuntimeConfigurationResolver? runtimeConfigurationResolver = null,
        IWorkspaceReadinessService? workspaceReadinessService = null,
        KodaClaw.Contracts.IProviderAccountRepository? accountRepository = null,
        IMcpHubService? mcpHubService = null,
        ISettingsRepository? settingsRepository = null,
        IMemorySessionSummaryService? sessionSummaryService = null)
    {
        _workspaceService = workspaceService;
        _dependenciesFactory = dependenciesFactory;
        _options = options ?? new MainSessionOptions();
        _diagnosticsService = diagnosticsService;
        _correlationContextAccessor = correlationContextAccessor;
        _approvalRepository = approvalRepository;
        _inboxRepository = inboxRepository;
        _pluginRegistryRepository = pluginRegistryRepository;
        _pluginLifecycleHost = pluginLifecycleHost;
        _runtimeConfigurationResolver = runtimeConfigurationResolver;
        _workspaceReadinessService = workspaceReadinessService;
        _accountRepository = accountRepository;
        _mcpHubService = mcpHubService;
        _settingsRepository = settingsRepository;
        _sessionSummaryService = sessionSummaryService;
    }

    public async Task<MainSessionHandle> EnsureMainSessionAsync(CancellationToken cancellationToken = default)
    {
        await _workspaceService.EnsureInitializedAsync(cancellationToken);

        var rotatedForWorkspace = _pendingWorkspaceRotation;
        if (_pendingWorkspaceRotation)
        {
            _pendingWorkspaceRotation = false;
            await RotateMainSessionAsync(cancellationToken);
        }

        var appConfig = await _workspaceService.LoadAppConfigAsync(cancellationToken);
        MainSessionHandle handle;

        if (string.IsNullOrWhiteSpace(appConfig.ActiveMainSessionId))
        {
            handle = await LoadOrCreateMainSessionAsync(GenerateSessionId(), cancellationToken);
        }
        else
        {
            handle = await LoadExistingOrFallbackAsync(appConfig.ActiveMainSessionId, cancellationToken);
        }

        if (!string.Equals(appConfig.ActiveMainSessionId, handle.SessionId, StringComparison.Ordinal))
        {
            await _workspaceService.SaveAppConfigAsync(
                appConfig with { ActiveMainSessionId = handle.SessionId },
                cancellationToken);
        }

        if (rotatedForWorkspace)
        {
            handle = handle with { WasRotatedForWorkspace = true };
        }

        return handle;
    }

    public async Task<string?> RotateMainSessionAsync(CancellationToken cancellationToken = default)
    {
        await _workspaceService.EnsureInitializedAsync(cancellationToken);

        var appConfig = await _workspaceService.LoadAppConfigAsync(cancellationToken);
        var previousSessionId = appConfig.ActiveMainSessionId;

        if (!string.IsNullOrWhiteSpace(previousSessionId))
        {
            if (_sessionSubscriptions.TryRemove(previousSessionId, out var subs))
            {
                subs.Dispose();
            }

            if (_agents.TryRemove(previousSessionId, out var agent))
            {
                var isResumed = _resumedSessionIds.Remove(previousSessionId);
                await TryGenerateSessionSummaryAsync(previousSessionId, "main", cancellationToken, isResumed);
                await agent.DisposeAsync();
            }
        }

        await _workspaceService.SaveAppConfigAsync(
            appConfig with { ActiveMainSessionId = null },
            cancellationToken);

        RecordDiagnosticEvent(
            eventType: "main_session.rotated",
            level: "info",
            message: "Main session rotated. Next EnsureMainSessionAsync will create a fresh session.",
            sessionId: previousSessionId ?? string.Empty);

        return string.IsNullOrWhiteSpace(previousSessionId) ? null : previousSessionId;
    }

    public async Task<ResumeSessionResponse> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _workspaceService.EnsureInitializedAsync(cancellationToken);

        var appConfig = await _workspaceService.LoadAppConfigAsync(cancellationToken);
        var previousSessionId = appConfig.ActiveMainSessionId;

        if (!string.IsNullOrWhiteSpace(previousSessionId))
        {
            if (_sessionSubscriptions.TryRemove(previousSessionId, out var subs))
            {
                subs.Dispose();
            }

            if (_agents.TryRemove(previousSessionId, out var agent))
            {
                await agent.DisposeAsync();
            }
        }

        _resumedSessionIds.Add(sessionId);

        await _workspaceService.SaveAppConfigAsync(
            appConfig with { ActiveMainSessionId = sessionId },
            cancellationToken);

        RecordDiagnosticEvent(
            eventType: "main_session.resumed",
            level: "info",
            message: $"Main session resumed to {sessionId}. Next EnsureMainSessionAsync will load from store.",
            sessionId: sessionId);

        return new ResumeSessionResponse(Ok: true, ResumedSessionId: sessionId);
    }

    public void RequestWorkspaceRotation()
    {
        _pendingWorkspaceRotation = true;
    }

    public string? TryGetApprovalIdForCall(string callId)
    {
        return _liveApprovals.TryGetValue(callId, out var ctx) ? ctx.ApprovalId : null;
    }

    public Task<ApprovalDecisionDispatchResult> ApproveApprovalAsync(
        string approvalId,
        CancellationToken cancellationToken = default)
    {
        return DispatchApprovalDecisionAsync(approvalId, approve: true, note: null, cancellationToken);
    }

    public Task<ApprovalDecisionDispatchResult> RejectApprovalAsync(
        string approvalId,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        return DispatchApprovalDecisionAsync(approvalId, approve: false, note, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscriptions in _sessionSubscriptions.Values)
        {
            subscriptions.Dispose();
        }

        _sessionSubscriptions.Clear();
        _liveApprovals.Clear();

        foreach (var agent in _agents.Values)
        {
            await agent.DisposeAsync();
        }

        _agents.Clear();
    }

    private async Task<ApprovalDecisionDispatchResult> DispatchApprovalDecisionAsync(
        string approvalId,
        bool approve,
        string? note,
        CancellationToken cancellationToken)
    {
        if (_approvalRepository == null || _inboxRepository == null)
        {
            return new ApprovalDecisionDispatchResult(
                ApprovalDecisionDispatchStatus.RuntimeUnavailable,
                Message: "Approval persistence is not configured.");
        }

        var approval = await _approvalRepository.GetByIdAsync(approvalId, cancellationToken);
        if (approval is null)
        {
            return new ApprovalDecisionDispatchResult(
                ApprovalDecisionDispatchStatus.NotFound,
                Message: "Approval was not found.");
        }

        if (approval.Status != ApprovalStatus.Pending)
        {
            return new ApprovalDecisionDispatchResult(
                ApprovalDecisionDispatchStatus.NotPending,
                approval,
                $"Approval is already {approval.Status}.");
        }

        var liveTarget = ResolveLiveApprovalTarget(approval);
        if (liveTarget.Status != ApprovalDecisionDispatchStatus.Completed || liveTarget.Target is null)
        {
            return new ApprovalDecisionDispatchResult(liveTarget.Status, approval, liveTarget.Message);
        }

        try
        {
            if (approve)
            {
                await liveTarget.Target.Agent.ApproveToolCallAsync(liveTarget.Target.CallId);
            }
            else
            {
                await liveTarget.Target.Agent.DenyToolCallAsync(liveTarget.Target.CallId, note);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not TaskCanceledException)
        {
            var errorMessage = ex.GetBaseException().Message;
            RecordDiagnosticEvent(
                eventType: "main_session.approval.dispatch_failed",
                level: "error",
                message: errorMessage,
                sessionId: liveTarget.Target.SessionId,
                correlationId: approval.CorrelationId,
                attributes: new Dictionary<string, string?>
                {
                    ["approvalId"] = approval.Id,
                    ["callId"] = liveTarget.Target.CallId,
                    ["decision"] = approve ? "allow" : "deny",
                });

            return new ApprovalDecisionDispatchResult(
                ApprovalDecisionDispatchStatus.LiveApprovalMissing,
                approval,
                errorMessage);
        }

        var expectedStatus = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        var decidedApproval = await WaitForApprovalDecisionAsync(approvalId, cancellationToken);
        if (decidedApproval is null)
        {
            RecordDiagnosticEvent(
                eventType: "main_session.approval.wait_timeout",
                level: "warning",
                message: $"Timed out waiting for approval decision to persist: approvalId={approvalId}",
                sessionId: liveTarget.Target.SessionId,
                correlationId: approval.CorrelationId,
                attributes: new Dictionary<string, string?> { ["approvalId"] = approvalId });

            return new ApprovalDecisionDispatchResult(
                ApprovalDecisionDispatchStatus.DecisionTimedOut,
                approval,
                "Timed out waiting for approval decision to persist.");
        }

        if (decidedApproval.Status != expectedStatus)
        {
            return new ApprovalDecisionDispatchResult(
                ApprovalDecisionDispatchStatus.NotPending,
                decidedApproval,
                $"Approval is already {decidedApproval.Status}.");
        }

        return new ApprovalDecisionDispatchResult(
            ApprovalDecisionDispatchStatus.Completed,
            decidedApproval);
    }

    private (ApprovalDecisionDispatchStatus Status, LiveApprovalTarget? Target, string? Message) ResolveLiveApprovalTarget(Approval approval)
    {
        var callId = TryGetApprovalCallId(approval);
        if (string.IsNullOrWhiteSpace(callId))
        {
            return (
                ApprovalDecisionDispatchStatus.LiveApprovalMissing,
                null,
                "Approval payload did not include a tool call id.");
        }

        if (_liveApprovals.TryGetValue(callId, out var liveContext))
        {
            if (_agents.TryGetValue(liveContext.SessionId, out var agent))
            {
                return (
                    ApprovalDecisionDispatchStatus.Completed,
                    new LiveApprovalTarget(liveContext.SessionId, callId, agent),
                    null);
            }

            return (
                ApprovalDecisionDispatchStatus.LiveSessionRequired,
                null,
                "A live runtime session is required for this approval.");
        }

        if (!string.IsNullOrWhiteSpace(approval.SessionId) && _agents.ContainsKey(approval.SessionId))
        {
            return (
                ApprovalDecisionDispatchStatus.LiveApprovalMissing,
                null,
                "Pending approval is no longer attached to an active approval waiter.");
        }

        return (
            ApprovalDecisionDispatchStatus.LiveSessionRequired,
            null,
            "A live runtime session is required for this approval.");
    }

    private async Task<Approval?> WaitForApprovalDecisionAsync(
        string approvalId,
        CancellationToken cancellationToken)
    {
        if (_approvalRepository == null)
        {
            return null;
        }

        for (var attempt = 0; attempt < ApprovalDecisionPollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var approval = await _approvalRepository.GetByIdAsync(approvalId, cancellationToken);
            if (approval is not null && approval.Status != ApprovalStatus.Pending)
            {
                return approval;
            }

            if (attempt + 1 < ApprovalDecisionPollAttempts)
            {
                await Task.Delay(ApprovalDecisionPollDelayMs, cancellationToken);
            }
        }

        var finalApproval = await _approvalRepository.GetByIdAsync(approvalId, cancellationToken);
        return finalApproval is not null && finalApproval.Status != ApprovalStatus.Pending
            ? finalApproval
            : null;
    }

    private static string? TryGetApprovalCallId(Approval approval)
    {
        if (!string.IsNullOrWhiteSpace(approval.PayloadJson))
        {
            try
            {
                using var payload = JsonDocument.Parse(approval.PayloadJson);
                if (payload.RootElement.TryGetProperty("callId", out var callIdProperty) &&
                    callIdProperty.ValueKind == JsonValueKind.String)
                {
                    return callIdProperty.GetString();
                }
            }
            catch (JsonException)
            {
                // Fall back to the deterministic approval id mapping if payload JSON is malformed.
            }
        }

        const string prefix = "approval-";
        return approval.Id.StartsWith(prefix, StringComparison.Ordinal)
            ? approval.Id[prefix.Length..]
            : null;
    }

    private async Task<MainSessionHandle> LoadExistingOrFallbackAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await LoadOrCreateMainSessionAsync(sessionId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not TaskCanceledException)
        {
            return await CreateFreshMainSessionAsync(
                GenerateSessionId(),
                FormatResumeFailureMessage(ex),
                sessionId,
                cancellationToken);
        }
    }

    private async Task<MainSessionHandle> LoadOrCreateMainSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_agents.TryGetValue(sessionId, out var cached))
        {
            return CreateHandle(sessionId, resumedFromStore: false, resumeFailureMessage: null, cached);
        }

        var sessionDirectory = _workspaceService.GetSessionDirectory(sessionId);
        Directory.CreateDirectory(sessionDirectory);
        var prompt = await BuildSystemPromptAsync(cancellationToken);

        var dependencies = _dependenciesFactory.Create(sessionId, sessionDirectory);
        if (await dependencies.Store.ExistsAsync(sessionId, cancellationToken))
        {
            var configuredModel = await ResolveConfiguredModelAsync(cancellationToken);
            var permissions = await ResolvePermissionsAsync(cancellationToken);
            var skillsPaths = _workspaceService.GetSkillsPaths();
            var contextWindowSize = await ResolveContextWindowSizeAsync(cancellationToken);
            try
            {
                var resumed = await AgentRuntime.ResumeFromStoreAsync(
                    sessionId,
                    dependencies,
                    options: new ResumeOptions { Strategy = RecoveryStrategy.Crash },
                    overrides: new AgentConfigOverrides
                    {
                        Model = configuredModel,
                        SystemPrompt = prompt.SystemPrompt,
                        Tools = _options.Tools,
                        Permissions = (permissions ?? new PermissionConfig()) with { SchemaHiddenTools = BuiltinSkills.SkillGatedTools },
                        SandboxOptions = new SandboxOptions
                        {
                            WorkingDirectory = _workspaceService.RootPath,
                            EnforceBoundary = true,
                            AllowPaths = skillsPaths,
                        },
                        Skills = new SkillsConfig
                        {
                            Paths = skillsPaths,
                            ValidateOnLoad = false,
                            AutoActivate = BuiltinSkills.ChatAutoActivate,
                        },
                        Context = new ContextManagerOptions
                        {
                            MaxTokens = (int)(contextWindowSize * _options.ContextCompressionTriggerRatio),
                            CompressToTokens = (int)(contextWindowSize * _options.ContextCompressionTargetRatio),
                            CompressionPrompt = _options.CompressionPrompt,
                            ToolResultCompression = new ToolResultCompressionOptions { Enabled = true },
                        },
                    },
                    cancellationToken: cancellationToken);

                await SessionPromptReportStore.WriteAsync(sessionDirectory, prompt, cancellationToken);
                TrackSession(sessionId, resumed);
                await CancelStalePendingApprovalsAsync(
                    sessionId,
                    "Canceled stale approvals after session resume.",
                    cancellationToken);
                RecordSessionResumed(sessionId);
                return CreateHandle(sessionId, resumedFromStore: true, resumeFailureMessage: null, resumed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not TaskCanceledException)
            {
                return await CreateFreshMainSessionAsync(
                    GenerateSessionId(),
                    FormatResumeFailureMessage(ex),
                    sessionId,
                    cancellationToken);
            }
        }

        return await CreateFreshMainSessionAsync(sessionId, null, null, cancellationToken);
    }

    private async Task<MainSessionHandle> CreateFreshMainSessionAsync(
        string sessionId,
        string? resumeFailureMessage,
        string? attemptedSessionId,
        CancellationToken cancellationToken)
    {
        var sessionDirectory = _workspaceService.GetSessionDirectory(sessionId);
        Directory.CreateDirectory(sessionDirectory);
        var prompt = await BuildSystemPromptAsync(cancellationToken);

        var dependencies = _dependenciesFactory.Create(sessionId, sessionDirectory);
        var sessionTools = await BuildSessionToolsAsync(sessionId, dependencies.ToolRegistry, cancellationToken);
        var configuredModel = await ResolveConfiguredModelAsync(cancellationToken);
        var permissions = await ResolvePermissionsAsync(cancellationToken);
        var maxIterations = await ResolveMaxIterationsAsync(cancellationToken);
        var contextWindowSize = await ResolveContextWindowSizeAsync(cancellationToken);
        var created = await AgentRuntime.CreateAsync(
            sessionId,
            CreateAgentConfig(sessionDirectory, sessionTools, configuredModel, prompt.SystemPrompt, contextWindowSize, permissions, maxIterations),
            dependencies,
            cancellationToken);

        await SessionPromptReportStore.WriteAsync(sessionDirectory, prompt, cancellationToken);
        TrackSession(sessionId, created);
        if (!string.IsNullOrWhiteSpace(attemptedSessionId))
        {
            await CancelStalePendingApprovalsAsync(
                attemptedSessionId,
                "Canceled stale approvals after session recovery fallback.",
                cancellationToken);
        }

        if (resumeFailureMessage is null)
        {
            RecordSessionCreated(sessionId);
        }
        else
        {
            RecordResumeFallback(sessionId, attemptedSessionId, resumeFailureMessage);
        }

        return CreateHandle(sessionId, resumedFromStore: false, resumeFailureMessage: resumeFailureMessage, created);
    }

    private async Task<IReadOnlyList<string>> BuildSessionToolsAsync(
        string sessionId,
        IToolRegistry? toolRegistry,
        CancellationToken cancellationToken)
    {
        var tools = new List<string>(_options.Tools);
        if (toolRegistry == null)
        {
            return tools;
        }

        var merged = new HashSet<string>(tools, StringComparer.OrdinalIgnoreCase);

        if (_pluginRegistryRepository != null && _pluginLifecycleHost != null)
        {
            IReadOnlyList<PluginRecord> candidates;
            try
            {
                candidates = await _pluginRegistryRepository.ListAsync(
                    new PluginQuery(
                        Type: PluginType.Tool,
                        Enabled: true,
                        RuntimeState: PluginRuntimeState.Running,
                        Limit: MaxPluginInjectionCandidates),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
            {
                RecordDiagnosticEvent(
                    eventType: "main_session.plugin_tools.query_failed",
                    level: "warning",
                    message: ex.GetBaseException().Message,
                    sessionId: sessionId);
                candidates = [];
            }

            var injectedPluginCount = 0;
            var injectedToolCount = 0;

            foreach (var candidate in candidates)
            {
                if (candidate.TrustState is not (PluginTrustState.Trusted or PluginTrustState.Signed))
                {
                    continue;
                }

                IReadOnlyList<ITool> pluginTools;
                try
                {
                    pluginTools = await _pluginLifecycleHost.GetToolsAsync(candidate.Id, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
                {
                    RecordDiagnosticEvent(
                        eventType: "main_session.plugin_tools.fetch_failed",
                        level: "warning",
                        message: ex.GetBaseException().Message,
                        sessionId: sessionId,
                        attributes: new Dictionary<string, string?>
                        {
                            ["pluginId"] = candidate.Id,
                        });
                    continue;
                }

                var injectedForCurrentPlugin = false;
                foreach (var pluginTool in pluginTools)
                {
                    if (string.IsNullOrWhiteSpace(pluginTool.Name) || !merged.Add(pluginTool.Name))
                    {
                        continue;
                    }

                    toolRegistry.Register(pluginTool);
                    tools.Add(pluginTool.Name);
                    injectedToolCount++;
                    injectedForCurrentPlugin = true;
                }

                if (injectedForCurrentPlugin)
                {
                    injectedPluginCount++;
                }
            }

            if (injectedToolCount > 0)
            {
                RecordDiagnosticEvent(
                    eventType: "main_session.plugin_tools.injected",
                    level: "info",
                    message: $"Injected {injectedToolCount} plugin tool(s) into a new main session.",
                    sessionId: sessionId,
                    attributes: new Dictionary<string, string?>
                    {
                        ["pluginCount"] = injectedPluginCount.ToString(),
                        ["toolCount"] = injectedToolCount.ToString(),
                    });
            }
        }

        if (_mcpHubService is not null)
        {
            var mcpResult = await _mcpHubService.InjectToolsAsync(sessionId, SessionKind.Main, toolRegistry, cancellationToken);
            foreach (var toolName in mcpResult.InjectedToolNames)
            {
                if (merged.Add(toolName))
                {
                    tools.Add(toolName);
                }
            }

            if (mcpResult.ToolCount > 0)
            {
                RecordDiagnosticEvent(
                    eventType: "main_session.workspace_mcp.injected",
                    level: "info",
                    message: $"McpHub injected {mcpResult.ToolCount} tool(s) from {mcpResult.ServerCount} server(s).",
                    sessionId: sessionId,
                    attributes: new Dictionary<string, string?>
                    {
                        ["serverCount"] = mcpResult.ServerCount.ToString(),
                        ["toolCount"] = mcpResult.ToolCount.ToString(),
                    });
            }
        }

        return tools;
    }

    private async Task<int> ResolveMaxIterationsAsync(CancellationToken cancellationToken)
    {
        if (_settingsRepository is null) return _options.MaxIterations;
        var settings = await _settingsRepository.GetAsync(cancellationToken);
        return settings.MainMaxIterations ?? _options.MaxIterations;
    }

    private AgentConfig CreateAgentConfig(
        string sessionDirectory,
        IReadOnlyList<string> tools,
        string model,
        string systemPrompt,
        int contextWindowSize,
        PermissionConfig? permissions = null,
        int? maxIterations = null)
    {
        var skillsPaths = _workspaceService.GetSkillsPaths();
        return new AgentConfig
        {
            Model = model,
            SystemPrompt = systemPrompt,
            MaxIterations = maxIterations ?? _options.MaxIterations,
            Tools = tools,
            Permissions = (permissions ?? _options.Permissions ?? new PermissionConfig()) with { SchemaHiddenTools = BuiltinSkills.SkillGatedTools },
            SandboxOptions = new SandboxOptions
            {
                WorkingDirectory = _workspaceService.RootPath,
                EnforceBoundary = true,
                AllowPaths = skillsPaths,
            },
            Skills = new SkillsConfig
            {
                Paths = skillsPaths,
                ValidateOnLoad = false,
                AutoActivate = BuiltinSkills.ChatAutoActivate,
            },
            Context = new ContextManagerOptions
            {
                MaxTokens = (int)(contextWindowSize * _options.ContextCompressionTriggerRatio),
                CompressToTokens = (int)(contextWindowSize * _options.ContextCompressionTargetRatio),
                CompressionPrompt = _options.CompressionPrompt,
                ToolResultCompression = new ToolResultCompressionOptions { Enabled = true },
            },
        };
    }

    private async Task<PermissionConfig> ResolvePermissionsAsync(CancellationToken cancellationToken)
    {
        if (_settingsRepository != null)
        {
            try
            {
                var settings = await _settingsRepository.GetAsync(cancellationToken);
                if (settings.AutoApproveToolCalls)
                {
                    return _options.Permissions with { Mode = "auto", RequireApprovalTools = [] };
                }
                return _options.Permissions;
            }
            catch
            {
                // Fall through to default if settings can't be read
            }
        }

        return _options.Permissions;
    }

    private Task<string> ResolveConfiguredModelAsync(CancellationToken cancellationToken) =>
        RuntimeProviderSelector.ResolveModelOrFallbackAsync(
            _runtimeConfigurationResolver,
            _options.Model,
            _accountRepository,
            cancellationToken);

    private static readonly IReadOnlyList<string> BaselineContextFiles =
    [
        KodaClawWorkspaceLayout.AgentsFile,
        KodaClawWorkspaceLayout.IdentityFile,
        KodaClawWorkspaceLayout.SoulFile,
        KodaClawWorkspaceLayout.OntologyFile,
        KodaClawWorkspaceLayout.UserFile,
        KodaClawWorkspaceLayout.MemoryFile,
    ];

    private async Task<int> ResolvePromptCharacterBudgetAsync(CancellationToken cancellationToken)
    {
        if (_accountRepository is null) return _options.MaxPromptCharacters;
        try
        {
            var resolved = await _accountRepository.ResolveDefaultForAsync(
                ModelCapabilitySet.Text, cancellationToken);
            if (resolved is null) return _options.MaxPromptCharacters;
            // Allocate 20% of usable context window to system prompt (× 4 chars/token); min = fallback default.
            var maxOutputCap = Math.Min(resolved.Model.MaxOutputTokens, Math.Min((int)(resolved.Model.ContextWindowSize * 0.20), 16_384));
            var usableTokens = Math.Max(resolved.Model.ContextWindowSize - maxOutputCap, 0);
            return Math.Max(usableTokens / 5 * 4, _options.MaxPromptCharacters);
        }
        catch
        {
            return _options.MaxPromptCharacters;
        }
    }

    /// <summary>
    /// Resolves the effective context window size from the configured model endpoint.
    /// Falls back to Options.DefaultContextWindowSize when no model registry is available.
    /// </summary>
    private async Task<int> ResolveContextWindowSizeAsync(CancellationToken cancellationToken)
    {
        if (_accountRepository is null) return _options.DefaultContextWindowSize;
        try
        {
            var resolved = await _accountRepository.ResolveDefaultForAsync(
                ModelCapabilitySet.Text, cancellationToken);
            if (resolved is null) return _options.DefaultContextWindowSize;
            var maxOutputCap = Math.Min(resolved.Model.MaxOutputTokens, Math.Min((int)(resolved.Model.ContextWindowSize * 0.20), 16_384));
            var available = resolved.Model.ContextWindowSize - maxOutputCap;
            return available > 0 ? available : _options.DefaultContextWindowSize;
        }
        catch
        {
            return _options.DefaultContextWindowSize;
        }
    }

    private async Task<PromptBuildResult> BuildSystemPromptAsync(CancellationToken cancellationToken)
    {
        var workspaceRoot = _workspaceService.RootPath;
        var workspaceDirectory = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.WorkspaceDirectory);
        var documents = new List<PromptContextDocument>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in BaselineContextFiles)
        {
            var absolutePath = Path.Combine(workspaceDirectory, file);
            await TryAddContextDocumentAsync(absolutePath, workspaceRoot, seenPaths, documents, cancellationToken);
        }

        var today = DateTimeOffset.Now.Date;
        var todayMemoryPath = Path.Combine(workspaceDirectory, "memory", $"{today:yyyy-MM-dd}.md");
        var yesterdayMemoryPath = Path.Combine(workspaceDirectory, "memory", $"{today.AddDays(-1):yyyy-MM-dd}.md");
        await TryAddContextDocumentAsync(yesterdayMemoryPath, workspaceRoot, seenPaths, documents, cancellationToken);
        await TryAddContextDocumentAsync(todayMemoryPath, workspaceRoot, seenPaths, documents, cancellationToken);

        var promptCharBudget = await ResolvePromptCharacterBudgetAsync(cancellationToken);
        var sessionStartedAt = DateTimeOffset.Now;
        var builder = new PromptBuilder(PromptProfiles.Main(_options.SystemPrompt))
            .WithCharacterBudget(promptCharBudget)
            .AddBody("Keep actions observable, local-first, and approval-aware.")
            .AddBody($"Session started at: {sessionStartedAt:yyyy-MM-dd HH:mm:ss zzz} ({sessionStartedAt.DayOfWeek}). Use get_current_datetime tool for a precise timestamp if the user asks later in the session.")
            .AddSection("Runtime Environment", RuntimeEnvironmentContext.BuildLines(workspaceRoot));

        if (_workspaceReadinessService is not null)
        {
            var readiness = await _workspaceReadinessService.GetReadinessAsync(cancellationToken);
            if (readiness.HasAnyGap)
            {
                var gaps = new List<string>();
                if (!readiness.IsIdentitySet) gaps.Add("IDENTITY.md (Koda's persona and role)");
                if (!readiness.IsSoulSet) gaps.Add("SOUL.md (behavior principles)");
                if (!readiness.IsUserSet) gaps.Add("USER.md (user profile and preferences)");

                builder.AddBody($"""
                    ## Workspace Guidance Active

                    The following workspace files still contain placeholder content: {string.Join(", ", gaps)}.

                    In this session, engage the user naturally to discover their preferences. Ask about who they are, how they work, and what they want Koda to optimize for. Once you have enough context, use workspace_protocol_update to update the relevant files. After updating, the new settings will take effect in the next session.

                    Do not ask all questions at once — have a natural conversation.
                    """);
            }
        }

        // Memory health checks
        var appConfigForHealth = await _workspaceService.LoadAppConfigAsync(cancellationToken);
        if (appConfigForHealth.LastConsolidationAt is null ||
            appConfigForHealth.LastConsolidationAt.Value.AddHours(48) < DateTimeOffset.UtcNow)
        {
            builder.AddBody("""
                ## Memory Notice

                最近 48 小时内未执行记忆整合。回答涉及历史信息的问题时，
                请同时使用 fs_grep 搜索 workspace/memory/ 目录下的日志文件，以确保信息完整。
                """);

            RecordDiagnosticEvent(
                eventType: "memory.consolidation_stale",
                level: "warning",
                message: $"Last consolidation: {appConfigForHealth.LastConsolidationAt?.ToString("o") ?? "never"}",
                sessionId: string.Empty);
        }

        // MEMORY.md size monitoring
        var memoryFilePath = Path.Combine(workspaceDirectory, KodaClawWorkspaceLayout.MemoryFile);
        if (File.Exists(memoryFilePath))
        {
            var memoryLines = await File.ReadAllLinesAsync(memoryFilePath, cancellationToken);
            if (memoryLines.Length > 200)
            {
                RecordDiagnosticEvent(
                    eventType: "memory.index_oversized",
                    level: "warning",
                    message: $"MEMORY.md has {memoryLines.Length} lines (recommended ≤200)",
                    sessionId: string.Empty);
            }
        }

        // Fire-and-forget: retry pending summaries from previous failed attempts
        if (_sessionSummaryService is MemorySessionSummaryService summaryService)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await summaryService.TryRetryPendingSummariesAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    RecordDiagnosticEvent(
                        eventType: "memory.pending_retry_background_failed",
                        level: "warning",
                        message: ex.GetBaseException().Message,
                        sessionId: string.Empty);
                }
            }, CancellationToken.None);
        }

        if (Environment.GetEnvironmentVariable("KODACLAW_DOCKER_MODE") == "true")
        {
            builder.AddBody("""
                ## File Persistence (Docker Deployment)
                Running inside a Docker container. Only paths under /data/ persist across restarts:
                - ~/  (→ /data/home/) — tool binaries, credentials, code outputs
                - workspace/  (→ /data/workspace/) — identity, memory, rules

                Save work outputs to ~/projects/ or workspace/outputs/.
                Do not write to /tmp/ or relative paths — they resolve to /app/ and vanish on restart.
                """);
        }

        return builder
            .AddContextDocuments(documents)
            .Build();
    }

    private static async Task TryAddContextDocumentAsync(
        string absolutePath,
        string workspaceRoot,
        HashSet<string> seenPaths,
        ICollection<PromptContextDocument> documents,
        CancellationToken cancellationToken)
    {
        if (!seenPaths.Add(absolutePath) || !File.Exists(absolutePath))
        {
            return;
        }

        var content = await File.ReadAllTextAsync(absolutePath, cancellationToken);
        var displayPath = absolutePath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase)
            ? absolutePath[workspaceRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : absolutePath;
        documents.Add(new PromptContextDocument(displayPath, content));
    }

    private void TrackSession(string sessionId, IAgent agent)
    {
        _agents[sessionId] = agent;
        AttachApprovalSubscriptions(sessionId, agent);
    }

    private void AttachApprovalSubscriptions(string sessionId, IAgent agent)
    {
        if (_approvalRepository == null || _inboxRepository == null || _sessionSubscriptions.ContainsKey(sessionId))
        {
            return;
        }

        var permissionRequired = agent.EventBus.OnControl<PermissionRequiredEvent>(evt =>
        {
            var correlationId = _correlationContextAccessor?.CorrelationId;
            var context = BuildLiveApprovalContext(sessionId, evt.Call, correlationId);
            _liveApprovals[evt.Call.Id] = context;
            _ = PersistPendingApprovalAsync(context);
        });

        var permissionDecided = agent.EventBus.OnControl<PermissionDecidedEvent>(evt =>
        {
            var correlationId = _correlationContextAccessor?.CorrelationId;
            _ = PersistApprovalDecisionAsync(sessionId, evt, correlationId);
        });

        _sessionSubscriptions[sessionId] = new SessionControlSubscriptions(permissionRequired, permissionDecided);
    }

    private async Task PersistPendingApprovalAsync(LiveApprovalContext context)
    {
        if (_approvalRepository == null || _inboxRepository == null)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            await _approvalRepository.UpsertAsync(new Approval(
                Id: context.ApprovalId,
                Kind: ApprovalKind.ExternalAction,
                Status: ApprovalStatus.Pending,
                Title: context.Title,
                Summary: context.Summary,
                Source: ApprovalSource,
                RequestedAt: now,
                UpdatedAt: now,
                SessionId: context.SessionId,
                CorrelationId: context.CorrelationId,
                InboxItemId: context.InboxItemId,
                PayloadJson: context.PayloadJson));

            await _inboxRepository.UpsertAsync(new InboxItem(
                Id: context.InboxItemId,
                Kind: InboxItemKind.Approval,
                Status: InboxItemStatus.Open,
                Title: context.Title,
                Summary: context.Summary,
                Source: ApprovalSource,
                CreatedAt: now,
                UpdatedAt: now,
                RequiresAction: true,
                Route: $"/approvals/{context.ApprovalId}",
                SessionId: context.SessionId,
                CorrelationId: context.CorrelationId,
                ApprovalId: context.ApprovalId,
                PayloadJson: context.PayloadJson));

            RecordDiagnosticEvent(
                eventType: "main_session.approval.requested",
                level: "info",
                message: $"Approval requested for tool '{context.ToolName}'.",
                sessionId: context.SessionId,
                correlationId: context.CorrelationId,
                attributes: new Dictionary<string, string?>
                {
                    ["approvalId"] = context.ApprovalId,
                    ["inboxItemId"] = context.InboxItemId,
                    ["callId"] = context.CallId,
                    ["toolName"] = context.ToolName,
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordDiagnosticEvent(
                eventType: "main_session.approval.persistence_failed",
                level: "error",
                message: ex.GetBaseException().Message,
                sessionId: context.SessionId,
                correlationId: context.CorrelationId,
                attributes: new Dictionary<string, string?>
                {
                    ["approvalId"] = context.ApprovalId,
                    ["callId"] = context.CallId,
                    ["toolName"] = context.ToolName,
                });
        }
    }

    private async Task PersistApprovalDecisionAsync(
        string sessionId,
        PermissionDecidedEvent evt,
        string? correlationId)
    {
        if (_approvalRepository == null || _inboxRepository == null)
        {
            return;
        }

        try
        {
            var context = _liveApprovals.TryRemove(evt.CallId, out var liveContext)
                ? liveContext
                : CreateFallbackApprovalContext(sessionId, evt.CallId, correlationId);

            var status = string.Equals(evt.Decision, "allow", StringComparison.OrdinalIgnoreCase)
                ? ApprovalStatus.Approved
                : ApprovalStatus.Rejected;
            var now = DateTimeOffset.UtcNow;

            var transitioned = await _approvalRepository.TransitionAsync(
                context.ApprovalId,
                status,
                now,
                decidedBy: evt.DecidedBy,
                decisionNote: evt.Note);

            if (!transitioned)
            {
                var existing = await _approvalRepository.GetByIdAsync(context.ApprovalId);
                if (existing == null)
                {
                    await _approvalRepository.UpsertAsync(new Approval(
                        Id: context.ApprovalId,
                        Kind: ApprovalKind.ExternalAction,
                        Status: ApprovalStatus.Pending,
                        Title: context.Title,
                        Summary: context.Summary,
                        Source: ApprovalSource,
                        RequestedAt: now,
                        UpdatedAt: now,
                        SessionId: context.SessionId,
                        CorrelationId: context.CorrelationId,
                        InboxItemId: context.InboxItemId,
                        PayloadJson: context.PayloadJson));

                    await _inboxRepository.UpsertAsync(new InboxItem(
                        Id: context.InboxItemId,
                        Kind: InboxItemKind.Approval,
                        Status: InboxItemStatus.Open,
                        Title: context.Title,
                        Summary: context.Summary,
                        Source: ApprovalSource,
                        CreatedAt: now,
                        UpdatedAt: now,
                        RequiresAction: true,
                        Route: $"/approvals/{context.ApprovalId}",
                        SessionId: context.SessionId,
                        CorrelationId: context.CorrelationId,
                        ApprovalId: context.ApprovalId,
                        PayloadJson: context.PayloadJson));

                    transitioned = await _approvalRepository.TransitionAsync(
                        context.ApprovalId,
                        status,
                        now,
                        decidedBy: evt.DecidedBy,
                        decisionNote: evt.Note);
                }
            }

            if (transitioned)
            {
                await _inboxRepository.UpdateStatusAsync(
                    context.InboxItemId,
                    InboxItemStatus.Resolved,
                    now,
                    resolvedAt: now);
            }

            RecordDiagnosticEvent(
                eventType: "main_session.approval.decided",
                level: status == ApprovalStatus.Approved ? "info" : "warning",
                message: $"Approval {status} for tool call '{evt.CallId}'.",
                sessionId: context.SessionId,
                correlationId: correlationId ?? context.CorrelationId,
                attributes: new Dictionary<string, string?>
                {
                    ["approvalId"] = context.ApprovalId,
                    ["inboxItemId"] = context.InboxItemId,
                    ["callId"] = evt.CallId,
                    ["decision"] = evt.Decision,
                    ["decidedBy"] = evt.DecidedBy,
                    ["decisionNote"] = evt.Note,
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordDiagnosticEvent(
                eventType: "main_session.approval.decision_persistence_failed",
                level: "error",
                message: ex.GetBaseException().Message,
                sessionId: sessionId,
                correlationId: correlationId,
                attributes: new Dictionary<string, string?>
                {
                    ["callId"] = evt.CallId,
                    ["decision"] = evt.Decision,
                });
        }
    }

    private async Task CancelStalePendingApprovalsAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_approvalRepository == null || _inboxRepository == null || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var pendingApprovals = await _approvalRepository.ListAsync(
            new ApprovalQuery(
                Status: ApprovalStatus.Pending,
                SessionId: sessionId,
                Limit: 200),
            cancellationToken);

        foreach (var approval in pendingApprovals)
        {
            var now = DateTimeOffset.UtcNow;
            var transitioned = await _approvalRepository.TransitionAsync(
                approval.Id,
                ApprovalStatus.Canceled,
                now,
                decidedBy: StaleApprovalDecisionBy,
                decisionNote: reason,
                cancellationToken);

            if (!transitioned)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(approval.InboxItemId))
            {
                await _inboxRepository.UpdateStatusAsync(
                    approval.InboxItemId,
                    InboxItemStatus.Resolved,
                    now,
                    resolvedAt: now,
                    cancellationToken);
            }

            RecordDiagnosticEvent(
                eventType: "main_session.approval.stale_canceled",
                level: "warning",
                message: reason,
                sessionId: sessionId,
                correlationId: approval.CorrelationId,
                attributes: new Dictionary<string, string?>
                {
                    ["approvalId"] = approval.Id,
                    ["inboxItemId"] = approval.InboxItemId,
                });
        }
    }

    private MainSessionHandle CreateHandle(string sessionId, bool resumedFromStore, string? resumeFailureMessage, IAgent agent)
    {
        return new MainSessionHandle(
            SessionId: sessionId,
            SessionKind: SessionKind.Main,
            SessionDirectory: _workspaceService.GetSessionDirectory(sessionId),
            ResumedFromStore: resumedFromStore,
            ResumeFailureMessage: resumeFailureMessage,
            Agent: agent);
    }

    private static LiveApprovalContext BuildLiveApprovalContext(
        string sessionId,
        ToolCallSnapshot call,
        string? correlationId)
    {
        var approvalId = GetApprovalId(call.Id);
        var inboxItemId = GetInboxItemId(call.Id);
        var inputPreview = BuildInputPreview(call.InputPreview);
        var payload = new ApprovalPayload(
            CallId: call.Id,
            ToolName: call.Name,
            InputPreview: inputPreview,
            PermissionMode: TryGetApprovalMeta(call.Approval.Meta, "mode"),
            Reason: TryGetApprovalMeta(call.Approval.Meta, "reason"));
        var title = $"Approval required for {call.Name}";
        var summary = string.IsNullOrWhiteSpace(inputPreview)
            ? $"Tool '{call.Name}' is waiting for approval."
            : $"Tool '{call.Name}' is waiting for approval: {Truncate(inputPreview, 180)}";

        return new LiveApprovalContext(
            SessionId: sessionId,
            CallId: call.Id,
            ApprovalId: approvalId,
            InboxItemId: inboxItemId,
            ToolName: call.Name,
            Title: title,
            Summary: summary,
            CorrelationId: correlationId,
            PayloadJson: JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static LiveApprovalContext CreateFallbackApprovalContext(
        string sessionId,
        string callId,
        string? correlationId)
    {
        var toolName = "unknown";
        return new LiveApprovalContext(
            SessionId: sessionId,
            CallId: callId,
            ApprovalId: GetApprovalId(callId),
            InboxItemId: GetInboxItemId(callId),
            ToolName: toolName,
            Title: $"Approval required for {toolName}",
            Summary: $"Tool call '{callId}' was waiting for approval.",
            CorrelationId: correlationId,
            PayloadJson: JsonSerializer.Serialize(
                new ApprovalPayload(
                    CallId: callId,
                    ToolName: toolName,
                    InputPreview: null,
                    PermissionMode: null,
                    Reason: null),
                JsonOptions));
    }

    private static string? BuildInputPreview(object? inputPreview)
    {
        if (inputPreview == null)
        {
            return null;
        }

        return inputPreview switch
        {
            string text => text,
            JsonElement json => json.GetRawText(),
            _ => inputPreview.ToString(),
        };
    }

    private static string? TryGetApprovalMeta(JsonElement? meta, string key)
    {
        if (meta is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty(key, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "…";
    }

    private static string GetApprovalId(string callId) => $"approval-{callId}";

    private static string GetInboxItemId(string callId) => $"inbox-approval-{callId}";

    private static string FormatResumeFailureMessage(Exception exception)
    {
        var root = exception.GetBaseException();
        return $"Resume failed: {root.GetType().Name}: {root.Message}";
    }

    private void RecordSessionCreated(string sessionId)
    {
        RecordDiagnosticEvent(
            eventType: "main_session.created",
            level: "info",
            message: "Created a fresh main session.",
            sessionId: sessionId);
    }

    private void RecordSessionResumed(string sessionId)
    {
        RecordDiagnosticEvent(
            eventType: "main_session.resumed",
            level: "info",
            message: "Resumed main session from store.",
            sessionId: sessionId);
    }

    private void RecordResumeFallback(
        string sessionId,
        string? attemptedSessionId,
        string resumeFailureMessage)
    {
        var attributes = new Dictionary<string, string?>
        {
            ["failedSessionId"] = attemptedSessionId,
            ["failureReason"] = resumeFailureMessage,
        };

        RecordDiagnosticEvent(
            eventType: "main_session.resume_fallback",
            level: "warning",
            message: resumeFailureMessage,
            sessionId: sessionId,
            attributes: attributes);
    }

    private async Task TryGenerateSessionSummaryAsync(
        string sessionId,
        string sessionType,
        CancellationToken cancellationToken,
        bool isResumed = false)
    {
        if (_sessionSummaryService is null)
        {
            return;
        }

        try
        {
            var sessionDirectory = _workspaceService.GetSessionDirectory(sessionId);
            var sessionsRoot = Directory.GetParent(sessionDirectory)?.FullName ?? sessionDirectory;
            var store = new JsonAgentStore(sessionsRoot);

            var messages = await store.LoadMessagesAsync(sessionId, cancellationToken);
            if (messages.Count == 0)
            {
                return;
            }

            var context = new MemorySessionSummaryContext(
                SessionId: sessionId,
                SessionType: sessionType,
                BindingId: null,
                Messages: messages,
                IsResumed: isResumed);

            await _sessionSummaryService.GenerateSummaryAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Let cancellation propagate
        }
        catch (Exception ex)
        {
            RecordDiagnosticEvent(
                eventType: "main_session.summary_generation_failed",
                level: "warning",
                message: ex.GetBaseException().Message,
                sessionId: sessionId);
        }
    }

    private void RecordDiagnosticEvent(
        string eventType,
        string level,
        string message,
        string sessionId,
        string? correlationId = null,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        if (_diagnosticsService == null)
        {
            return;
        }

        var diagnosticEvent = new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: DiagnosticSource,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: correlationId ?? _correlationContextAccessor?.CorrelationId,
            SessionId: sessionId,
            Attributes: attributes);

        _diagnosticsService.Record(diagnosticEvent);
    }

    private static string GenerateSessionId()
    {
        return $"main-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..32];
    }

    private sealed record LiveApprovalTarget(string SessionId, string CallId, IAgent Agent);

    private sealed record SessionControlSubscriptions(
        IDisposable PermissionRequired,
        IDisposable PermissionDecided) : IDisposable
    {
        public void Dispose()
        {
            PermissionRequired.Dispose();
            PermissionDecided.Dispose();
        }
    }

    private sealed record LiveApprovalContext(
        string SessionId,
        string CallId,
        string ApprovalId,
        string InboxItemId,
        string ToolName,
        string Title,
        string Summary,
        string? CorrelationId,
        string PayloadJson);

    private sealed record ApprovalPayload(
        string CallId,
        string ToolName,
        string? InputPreview,
        string? PermissionMode,
        string? Reason);
}
