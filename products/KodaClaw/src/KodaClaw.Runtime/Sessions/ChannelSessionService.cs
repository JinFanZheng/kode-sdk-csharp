using System.Collections.Concurrent;
using System.Diagnostics;
using KodaClaw.Contracts;
using KodaClaw.McpHub;
using KodaClaw.ModelHub;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Agent;
using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Diagnostics;
using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Store.Json;
using AgentRuntime = Kode.Agent.Sdk.Core.Agent.Agent;

namespace KodaClaw.Runtime;

public sealed class ChannelSessionService : IChannelSessionService, IAsyncDisposable
{
    private const string ThreadSummaryFileName = "SUMMARY.md";

    private const string DiagnosticSource = "channel_session";

    private readonly IWorkspaceService _workspaceService;
    private readonly IMainSessionAgentDependenciesFactory _dependenciesFactory;
    private readonly ChannelSessionOptions _options;
    private readonly IRuntimeConfigurationResolver? _runtimeConfigurationResolver;
    private readonly IProviderAccountRepository? _accountRepository;
    private readonly IMcpHubService? _mcpHubService;
    private readonly IThreadBindingRepository? _threadBindingRepository;
    private readonly IMemorySessionSummaryService? _sessionSummaryService;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly ISettingsRepository? _settingsRepository;
    private readonly Dictionary<string, IAgent> _agents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionModels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _pendingModelOverrides = new(StringComparer.Ordinal);

    public ChannelSessionService(
        IWorkspaceService workspaceService,
        IMainSessionAgentDependenciesFactory dependenciesFactory,
        ChannelSessionOptions? options = null,
        IRuntimeConfigurationResolver? runtimeConfigurationResolver = null,
        IProviderAccountRepository? accountRepository = null,
        IMcpHubService? mcpHubService = null,
        IThreadBindingRepository? threadBindingRepository = null,
        IMemorySessionSummaryService? sessionSummaryService = null,
        IDiagnosticsService? diagnosticsService = null,
        ISettingsRepository? settingsRepository = null)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _dependenciesFactory = dependenciesFactory ?? throw new ArgumentNullException(nameof(dependenciesFactory));
        _options = options ?? new ChannelSessionOptions();
        _runtimeConfigurationResolver = runtimeConfigurationResolver;
        _accountRepository = accountRepository;
        _mcpHubService = mcpHubService;
        _threadBindingRepository = threadBindingRepository;
        _sessionSummaryService = sessionSummaryService;
        _diagnosticsService = diagnosticsService;
        _settingsRepository = settingsRepository;
    }

    public async Task<ChannelSessionHandle> EnsureChannelSessionAsync(
        ThreadBinding binding,
        ChannelPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(policy);
        ValidateBindingAndPolicy(binding, policy);

        var snapshot = await _workspaceService.EnsureInitializedAsync(cancellationToken);
        var contextDocuments = await LoadContextDocumentsAsync(snapshot.RootPath, binding, policy, cancellationToken);
        var promptCharBudget = await ResolvePromptCharacterBudgetAsync(cancellationToken);
        var prompt = BuildSystemPrompt(binding, policy, contextDocuments, promptCharBudget);
        var systemPrompt = prompt.SystemPrompt;
        var sessionDirectory = _workspaceService.GetSessionDirectory(binding.SessionId);

        if (_agents.TryGetValue(binding.SessionId, out var cached))
        {
            await SessionPromptReportStore.WriteAsync(sessionDirectory, prompt, cancellationToken);
            return CreateHandle(
                binding,
                sessionDirectory,
                resumedFromStore: false,
                resumeFailureMessage: null,
                cached);
        }

        Directory.CreateDirectory(sessionDirectory);
        await SessionPromptReportStore.WriteAsync(sessionDirectory, prompt, cancellationToken);

        var dependencies = _dependenciesFactory.Create(binding.SessionId, sessionDirectory);

        // KC-2201: Session timeout policy — skip resume if the thread has been inactive
        // for more than SessionTimeoutDays days. This avoids stale context from old sessions.
        // KC-5002: DM sessions never time out — continuity is the value, same as the main session.
        // Only Group sessions are subject to the timeout, as group context becomes stale.
        var isSessionTimedOut = binding.ThreadType == ChannelThreadType.Group
            && _options.SessionTimeoutDays > 0
            && binding.LastInboundAt.HasValue
            && binding.LastInboundAt.Value < DateTimeOffset.UtcNow.AddDays(-_options.SessionTimeoutDays);

        if (isSessionTimedOut)
        {
            RecordDiagnosticEvent(
                eventType: "channel_session.timeout",
                level: "warning",
                message: $"Channel session timed out (inactive >{_options.SessionTimeoutDays}d), will create fresh: bindingId={binding.Id}",
                sessionId: binding.SessionId);
        }

        var maxIterations = await ResolveMaxIterationsAsync(cancellationToken);
        var contextWindowSize = await ResolveContextWindowSizeAsync(cancellationToken);

        // 4a: 统一计算 explicitOverride（内存快路径 + 重启后 DB 兜底）
        string? explicitOverride = null;
        if (_pendingModelOverrides.TryRemove(binding.Id, out var memOverride))
            explicitOverride = memOverride;                   // 同进程快路径
        else if (binding.PendingModelOverride is not null)
            explicitOverride = binding.PendingModelOverride;  // 重启后从 DB 读取

        if (!isSessionTimedOut && await dependencies.Store.ExistsAsync(binding.SessionId, cancellationToken))
        {
            // Resume 路径：explicitOverride → ActiveModelId → 默认模型
            var configuredModel = explicitOverride
                ?? binding.ActiveModelId
                ?? await ResolveConfiguredModelAsync(cancellationToken);

            var resumeTools = await BuildSessionToolsAsync(binding.SessionId, dependencies.ToolRegistry, binding.ThreadType, cancellationToken);
            var skillsPaths = _workspaceService.GetSkillsPaths();
            try
            {
                var resumed = await AgentRuntime.ResumeFromStoreAsync(
                    binding.SessionId,
                    dependencies,
                    options: new ResumeOptions { Strategy = RecoveryStrategy.Crash },
                    overrides: new AgentConfigOverrides
                    {
                        Model = configuredModel,
                        SystemPrompt = systemPrompt,
                        Tools = resumeTools,
                        Permissions = (_options.Permissions ?? new PermissionConfig()) with { SchemaHiddenTools = BuiltinSkills.SkillGatedTools },
                        SandboxOptions = new SandboxOptions
                        {
                            // KC-5003: DM sessions use workspace root; Group sessions stay isolated.
                            WorkingDirectory = binding.ThreadType == ChannelThreadType.DirectMessage
                                ? _workspaceService.RootPath
                                : sessionDirectory,
                            EnforceBoundary = true,
                            AllowPaths = skillsPaths,
                        },
                        Skills = new SkillsConfig
                        {
                            Paths = skillsPaths,
                            ValidateOnLoad = false,
                            AutoActivate = BuiltinSkills.ChannelAutoActivate,
                        },
                        Context = new ContextManagerOptions
                        {
                            MaxTokens = (int)(contextWindowSize * _options.ContextCompressionTriggerRatio),
                            CompressToTokens = (int)(contextWindowSize * _options.ContextCompressionTargetRatio),
                            CompressionPrompt = binding.ThreadType == ChannelThreadType.DirectMessage
                                ? _options.DmCompressionPrompt
                                : _options.GroupCompressionPrompt,
                            ToolResultCompression = new ToolResultCompressionOptions { Enabled = true },
                        },
                    },
                    cancellationToken: cancellationToken);

                _agents[binding.SessionId] = resumed;
                _sessionModels[binding.SessionId] = configuredModel;
                // 4b: 持久化 ActiveModelId，清零 PendingModelOverride
                if (_threadBindingRepository is not null)
                    await _threadBindingRepository.UpsertAsync(
                        binding with { ActiveModelId = configuredModel, PendingModelOverride = null, UpdatedAt = DateTimeOffset.UtcNow },
                        cancellationToken);
                RecordDiagnosticEvent(
                    eventType: "channel_session.resumed",
                    level: "info",
                    message: $"Channel session resumed from store: bindingId={binding.Id}",
                    sessionId: binding.SessionId);
                return CreateHandle(
                    binding,
                    sessionDirectory,
                    resumedFromStore: true,
                    resumeFailureMessage: null,
                    resumed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
            {
                var createdAfterFallback = await AgentRuntime.CreateAsync(
                    binding.SessionId,
                    CreateAgentConfig(sessionDirectory, systemPrompt, configuredModel, contextWindowSize, resumeTools, isDirectMessage: binding.ThreadType == ChannelThreadType.DirectMessage, maxIterations: maxIterations),
                    dependencies,
                    cancellationToken);

                _agents[binding.SessionId] = createdAfterFallback;
                _sessionModels[binding.SessionId] = configuredModel;
                // 4b: 持久化 ActiveModelId，清零 PendingModelOverride
                if (_threadBindingRepository is not null)
                    await _threadBindingRepository.UpsertAsync(
                        binding with { ActiveModelId = configuredModel, PendingModelOverride = null, UpdatedAt = DateTimeOffset.UtcNow },
                        cancellationToken);
                RecordDiagnosticEvent(
                    eventType: "channel_session.resume_fallback",
                    level: "warning",
                    message: $"Channel session resume failed, created fresh: bindingId={binding.Id} error={ex.GetBaseException().Message}",
                    sessionId: binding.SessionId);
                return CreateHandle(
                    binding,
                    sessionDirectory,
                    resumedFromStore: false,
                    resumeFailureMessage: FormatResumeFailureMessage(ex),
                    createdAfterFallback);
            }
        }

        // Fresh create 路径：explicitOverride → 默认模型（不使用 ActiveModelId，避免超时后继承旧模型）
        {
            var configuredModel = explicitOverride
                ?? await ResolveConfiguredModelAsync(cancellationToken);

            var sessionTools = await BuildSessionToolsAsync(binding.SessionId, dependencies.ToolRegistry, binding.ThreadType, cancellationToken);
            var created = await AgentRuntime.CreateAsync(
                binding.SessionId,
                CreateAgentConfig(sessionDirectory, systemPrompt, configuredModel, contextWindowSize, sessionTools, isDirectMessage: binding.ThreadType == ChannelThreadType.DirectMessage, maxIterations: maxIterations),
                dependencies,
                cancellationToken);

            _agents[binding.SessionId] = created;
            _sessionModels[binding.SessionId] = configuredModel;
            // 4b: 持久化 ActiveModelId，清零 PendingModelOverride
            if (_threadBindingRepository is not null)
                await _threadBindingRepository.UpsertAsync(
                    binding with { ActiveModelId = configuredModel, PendingModelOverride = null, UpdatedAt = DateTimeOffset.UtcNow },
                    cancellationToken);
            RecordDiagnosticEvent(
                eventType: "channel_session.created",
                level: "info",
                message: $"Channel session created: bindingId={binding.Id}",
                sessionId: binding.SessionId);
            return CreateHandle(
                binding,
                sessionDirectory,
                resumedFromStore: false,
                resumeFailureMessage: null,
                created);
        }
    }

    public async Task<ChannelTurnExecutionResult> RunInboundTurnAsync(
        ThreadBinding binding,
        ChannelPolicy policy,
        ChannelEventEnvelope envelope,
        bool hasExplicitMention,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var handle = await EnsureChannelSessionAsync(binding, policy, cancellationToken);
        var prompt = BuildInboundTurnPrompt(binding, envelope, hasExplicitMention);
        AgentRunResult runResult;
        var sessionLock = _sessionLocks.GetOrAdd(handle.SessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            using var turnActivity = KodeAgentActivitySource.Source.StartActivity("channel.turn");
            turnActivity?.SetTag("channel.binding_id", binding.Id);
            turnActivity?.SetTag("channel.connector_kind", binding.ConnectorKind.ToString());
            turnActivity?.SetTag("channel.session_id", handle.SessionId);

            runResult = envelope.MediaAttachments is { Count: > 0 } && _accountRepository != null
                ? await RunMultimodalTurnAsync(handle, prompt, envelope.MediaAttachments, cancellationToken)
                : await handle.Agent.RunAsync(prompt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
        {
            RecordDiagnosticEvent(
                eventType: "channel_session.turn_failed",
                level: "error",
                message: $"Channel turn failed: bindingId={binding.Id} error={ex.GetBaseException().Message}",
                sessionId: binding.SessionId);
            throw;
        }
        finally
        {
            try { sessionLock.Release(); }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }

        var turnLevel = runResult.StopReason == StopReason.Error ? "warning" : runResult.StopReason == StopReason.EndTurn ? "debug" : "info";
        RecordDiagnosticEvent(
            eventType: "channel_session.turn_completed",
            level: turnLevel,
            message: $"Channel turn completed: bindingId={binding.Id} stopReason={runResult.StopReason}",
            sessionId: binding.SessionId);

        return new ChannelTurnExecutionResult(
            Session: handle,
            RunResult: runResult,
            RawResponse: runResult.Response ?? string.Empty,
            Proposal: null,
            HasExplicitMention: hasExplicitMention);
    }

    /// <summary>
    /// Runs a multimodal turn: checks if the current model supports Image input,
    /// and if so, sends text + image content blocks; otherwise falls back to text-only prompt.
    /// </summary>
    private async Task<AgentRunResult> RunMultimodalTurnAsync(
        ChannelSessionHandle handle,
        string prompt,
        IReadOnlyList<MediaReference> attachments,
        CancellationToken cancellationToken)
    {
        var hasImage = false;
        try
        {
            var endpoint = await _accountRepository!.ResolveDefaultForAsync(
                ModelCapabilitySet.Image, cancellationToken);
            hasImage = endpoint != null;
        }
        catch
        {
            // Image capability check failed, fall back to text-only
        }

        if (!hasImage)
        {
            // Model doesn't support Image input — fall back to text-only prompt with media info
            return await handle.Agent.RunAsync(prompt, cancellationToken);
        }

        // Build multimodal content: text prompt + image(s) as base64
        var parts = new List<ContentBlock> { new TextContent { Text = prompt } };

        foreach (var attachment in attachments)
        {
            if (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var mediaPath = Path.Combine(_workspaceService.RootPath, "media", $"{attachment.MediaId}.bin");
                    if (File.Exists(mediaPath))
                    {
                        var bytes = await File.ReadAllBytesAsync(mediaPath, cancellationToken);
                        var base64 = Convert.ToBase64String(bytes);
                        parts.Add(ImageContent.FromBase64(attachment.ContentType, base64));
                    }
                    else
                    {
                        parts.Add(new TextContent { Text = $"[Image {attachment.FileName ?? attachment.MediaId} not found on disk]" });
                    }
                }
                catch (Exception ex)
                {
                    // Image read failed, skip this attachment
                    parts.Add(new TextContent { Text = $"[Image {attachment.FileName ?? attachment.MediaId} failed to load: {ex.Message}]" });
                }
            }
            else
            {
                // Non-image attachment: just include metadata as text
                parts.Add(new TextContent { Text = FormatSingleAttachment(attachment) });
            }
        }

        return await handle.Agent.RunAsync(parts, cancellationToken);
    }

    private static string FormatSingleAttachment(MediaReference attachment)
    {
        var sizeStr = attachment.SizeBytes.HasValue
            ? $"{attachment.SizeBytes.Value / 1024.0:F1} KB"
            : "unknown size";
        return $"[Media: {attachment.ContentType}, {sizeStr}, file={attachment.FileName ?? attachment.MediaId}]";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var agent in _agents.Values)
        {
            try
            {
                await agent.DisposeAsync();
            }
            catch (ObjectDisposedException)
            {
                // Reused runtime handles may already have released the agent.
            }
        }

        _agents.Clear();

        foreach (var semaphore in _sessionLocks.Values)
        {
            semaphore.Dispose();
        }

        _sessionLocks.Clear();
    }

    private async Task<IReadOnlyList<PromptContextDocument>> LoadContextDocumentsAsync(
        string workspaceRoot,
        ThreadBinding binding,
        ChannelPolicy policy,
        CancellationToken cancellationToken)
    {
        var scope = ResolveEffectiveScope(policy);
        var documents = new List<PromptContextDocument>();
        var seenPaths = new HashSet<string>(GetPathComparer());
        var workspaceDirectory = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.WorkspaceDirectory);

        if (scope.LoadAgents)
        {
            await TryAddContextDocumentAsync(
                Path.Combine(workspaceDirectory, KodaClawWorkspaceLayout.AgentsFile),
                workspaceRoot,
                seenPaths,
                documents,
                cancellationToken);
        }

        if (scope.LoadIdentity)
        {
            await TryAddContextDocumentAsync(
                Path.Combine(workspaceDirectory, KodaClawWorkspaceLayout.IdentityFile),
                workspaceRoot,
                seenPaths,
                documents,
                cancellationToken);
        }

        if (scope.LoadSoul)
        {
            await TryAddContextDocumentAsync(
                Path.Combine(workspaceDirectory, KodaClawWorkspaceLayout.SoulFile),
                workspaceRoot,
                seenPaths,
                documents,
                cancellationToken);
            await TryAddContextDocumentAsync(
                Path.Combine(workspaceDirectory, KodaClawWorkspaceLayout.OntologyFile),
                workspaceRoot,
                seenPaths,
                documents,
                cancellationToken);
        }

        if (scope.LoadUserProfile)
        {
            await TryAddContextDocumentAsync(
                Path.Combine(workspaceDirectory, KodaClawWorkspaceLayout.UserFile),
                workspaceRoot,
                seenPaths,
                documents,
                cancellationToken);
        }

        // KC-5001: DM sessions load long-term memory (MEMORY.md) — same as main session.
        if (scope.LoadLongTermMemory)
        {
            await TryAddContextDocumentAsync(
                Path.Combine(workspaceDirectory, KodaClawWorkspaceLayout.MemoryFile),
                workspaceRoot,
                seenPaths,
                documents,
                cancellationToken);

            // Load yesterday's and today's daily memory for session continuity.
            var today = DateTimeOffset.Now.Date;
            await TryAddContextDocumentAsync(
                Path.Combine(workspaceDirectory, "memory", $"{today.AddDays(-1):yyyy-MM-dd}.md"),
                workspaceRoot,
                seenPaths,
                documents,
                cancellationToken);
            await TryAddContextDocumentAsync(
                Path.Combine(workspaceDirectory, "memory", $"{today:yyyy-MM-dd}.md"),
                workspaceRoot,
                seenPaths,
                documents,
                cancellationToken);
        }

        if (scope.LoadRecentThreadSummary)
        {
            await TryAddContextDocumentAsync(
                Path.Combine(
                    workspaceDirectory,
                    "channels",
                    binding.Id,
                    ThreadSummaryFileName),
                workspaceRoot,
                seenPaths,
                documents,
                cancellationToken);
        }

        return documents;
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
        documents.Add(new PromptContextDocument(ToDisplayPath(workspaceRoot, absolutePath), content));
    }

    public async Task<string> RotateSessionAsync(
        ThreadBinding binding,
        string? modelOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (_agents.TryGetValue(binding.SessionId, out var agent))
        {
            await TryGenerateChannelSessionSummaryAsync(binding, cancellationToken);
            _agents.Remove(binding.SessionId);
            _sessionModels.TryRemove(binding.SessionId, out _);
            if (_sessionLocks.TryRemove(binding.SessionId, out var removedLock))
            {
                removedLock.Dispose();
            }
            await agent.DisposeAsync();
        }

        // 4c: 先无条件清除旧值，再按需写入新值，保证内存与 DB 始终一致
        _pendingModelOverrides.TryRemove(binding.Id, out _);
        if (modelOverride is not null)
            _pendingModelOverrides[binding.Id] = modelOverride;

        var newSessionId = GenerateChannelSessionId(binding.ConnectorKind, binding.ThreadType);

        if (_threadBindingRepository is not null)
        {
            await _threadBindingRepository.UpsertAsync(
                binding with
                {
                    SessionId = newSessionId,
                    PendingModelOverride = modelOverride,  // null 表示无 override，覆盖旧值
                    ActiveModelId = null,                  // 旧 session 已蒸发，新 session 尚未创建
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                cancellationToken);
        }

        return newSessionId;
    }

    public async Task<IDisposable> AcquireSessionLockAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var semaphore = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new SemaphoreReleaser(semaphore);
    }

    public string BuildPrompt(ThreadBinding binding, ChannelEventEnvelope envelope, bool hasExplicitMention)
    {
        return BuildInboundTurnPrompt(binding, envelope, hasExplicitMention);
    }

    public Task<AgentSessionState?> GetSessionStateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        // Lock-free read: /status only snapshots simple value-type fields
        // (RuntimeState enum, BreakpointState enum, StepCount int). No need
        // to contend with the turn-execution SemaphoreSlim — that causes
        // /status to hang when a turn is running.
        if (!_agents.TryGetValue(sessionId, out var agent))
        {
            return Task.FromResult<AgentSessionState?>(null);
        }

        // Cast to concrete type to access extended status fields.
        // All new fields use volatile / Interlocked reads — safe without locking.
        var concreteAgent = agent as AgentRuntime;

        return Task.FromResult<AgentSessionState?>(new AgentSessionState(
            SessionId: sessionId,
            RuntimeState: agent.RuntimeState,
            BreakpointState: agent.BreakpointState,
            StepCount: agent.StepCount,
            CurrentToolName: concreteAgent?.CurrentExecutingToolName,
            MessageCount: concreteAgent?.MessageCount ?? 0,
            PendingQueueCount: concreteAgent?.PendingQueueCount ?? 0,
            IterationCount: concreteAgent?.IterationCount ?? 0,
            MaxIterations: concreteAgent?.MaxIterations ?? 0,
            TurnStartedAt: concreteAgent?.TurnStartedAt,
            LastActivityAt: concreteAgent?.LastActivityAt));
    }

    public async Task<string> StopCurrentTurnAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_agents.TryGetValue(sessionId, out var agent))
        {
            return "当前没有正在执行的任务。";
        }

        if (agent.RuntimeState == Kode.Agent.Sdk.Core.Abstractions.AgentRuntimeState.Ready)
        {
            return "当前没有正在执行的任务。";
        }

        // InterruptAsync is defined on the concrete Agent class, not on IAgent interface.
        if (agent is AgentRuntime concreteAgent)
        {
            await concreteAgent.InterruptAsync(cancellationToken: cancellationToken);
            return "已发送停止信号，正在中断当前任务...";
        }

        return "当前没有正在执行的任务。";
    }

    public Task<IReadOnlyList<string>> GetSessionToolNamesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        // Return the configured tool list from session options.
        // This reflects which tools are in the allow-list for the session type.
        if (!_agents.ContainsKey(sessionId))
            return Task.FromResult<IReadOnlyList<string>>([]);

        return Task.FromResult<IReadOnlyList<string>>(_options.Tools.ToList());
    }

    public async Task<string?> GetSessionModelAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_sessionModels.TryGetValue(sessionId, out var model))
            return model;

        // 4d: 重启后 _sessionModels 为空时，从持久化 binding 回查
        if (_threadBindingRepository is not null)
        {
            var binding = await _threadBindingRepository.GetBySessionIdAsync(sessionId, cancellationToken);
            return binding?.ActiveModelId;
        }

        return null;
    }

    public async Task<string> CompressSessionContextAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_agents.TryGetValue(sessionId, out var agent))
            return "会话不存在或尚未创建。";

        // Refuse to compress while the agent is actively processing a turn.
        if (agent.RuntimeState == Kode.Agent.Sdk.Core.Abstractions.AgentRuntimeState.Working)
            return "Agent 正在处理消息，请等待本轮结束后再执行压缩。";

        // Acquire session lock to avoid concurrent modifications.
        var semaphore = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var compressed = await agent.ForceCompressAsync(cancellationToken);
            return compressed
                ? "上下文压缩完成，旧消息已摘要归档。"
                : "无法压缩（可能缺少摘要模型或消息历史为空）。";
        }
        finally
        {
            try { semaphore.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    public async Task<IReadOnlyList<Message>> GetSessionMessagesAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            // JsonAgentStore.LoadMessagesAsync uses a WAL write strategy: concurrent reads are safe
            // even while the main session is actively running and saving new messages.
            // JsonAgentStore uses agentId as a sub-directory under its base path, so the base
            // must be the PARENT of the session directory (i.e., the sessions root), not the
            // session directory itself — otherwise we'd look one level too deep.
            var sessionDirectory = _workspaceService.GetSessionDirectory(sessionId);
            var sessionsRoot = Directory.GetParent(sessionDirectory)?.FullName ?? sessionDirectory;
            var store = new JsonAgentStore(sessionsRoot);
            return await store.LoadMessagesAsync(sessionId, cancellationToken);
        }
        catch
        {
            // Session directory may not exist yet (session not started) — return empty.
            return [];
        }
    }

    private async Task TryGenerateChannelSessionSummaryAsync(
        ThreadBinding binding,
        CancellationToken cancellationToken)
    {
        if (_sessionSummaryService is null)
        {
            return;
        }

        try
        {
            var sessionDirectory = _workspaceService.GetSessionDirectory(binding.SessionId);
            var sessionsRoot = Directory.GetParent(sessionDirectory)?.FullName ?? sessionDirectory;
            var store = new JsonAgentStore(sessionsRoot);

            var messages = await store.LoadMessagesAsync(binding.SessionId, cancellationToken);
            if (messages.Count == 0)
            {
                return;
            }

            var context = new MemorySessionSummaryContext(
                SessionId: binding.SessionId,
                SessionType: "channel",
                BindingId: binding.Id,
                Messages: messages);

            await _sessionSummaryService.GenerateSummaryAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Let cancellation propagate
        }
        catch
        {
            // Summary generation failure must not block session rotation
        }
    }

    private static string GenerateChannelSessionId(ChannelConnectorKind connectorKind, ChannelThreadType threadType)
    {
        var kind = connectorKind.ToString().ToLowerInvariant();
        var thread = threadType == ChannelThreadType.DirectMessage ? "dm" : "group";
        return $"channel-{kind}-{thread}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
    }

    private async Task<IReadOnlyList<string>> BuildSessionToolsAsync(
        string sessionId,
        IToolRegistry? toolRegistry,
        ChannelThreadType threadType,
        CancellationToken cancellationToken)
    {
        var tools = new List<string>(_options.Tools);

        // KC-5003: DM sessions get workspace write-back tools — owner trust level equals main session.
        if (threadType == ChannelThreadType.DirectMessage)
        {
            var dmTools = new HashSet<string>(tools, StringComparer.OrdinalIgnoreCase);
            if (dmTools.Add("workspace_protocol_update")) tools.Add("workspace_protocol_update");
            if (dmTools.Add("workspace_memory_append")) tools.Add("workspace_memory_append");
        }

        if (_mcpHubService is null || toolRegistry is null)
        {
            return tools;
        }

        var merged = new HashSet<string>(tools, StringComparer.OrdinalIgnoreCase);
        var sessionKind = threadType == ChannelThreadType.DirectMessage
            ? SessionKind.ChannelDirectMessage
            : SessionKind.ChannelGroup;
        var mcpResult = await _mcpHubService.InjectToolsAsync(sessionId, sessionKind, toolRegistry, cancellationToken);
        foreach (var toolName in mcpResult.InjectedToolNames)
        {
            if (merged.Add(toolName))
            {
                tools.Add(toolName);
            }
        }

        return tools;
    }

    private async Task<int> ResolveMaxIterationsAsync(CancellationToken cancellationToken)
    {
        if (_settingsRepository is null) return _options.MaxIterations;
        var settings = await _settingsRepository.GetAsync(cancellationToken);
        return settings.ChannelMaxIterations ?? _options.MaxIterations;
    }

    private AgentConfig CreateAgentConfig(
        string sessionDirectory,
        string systemPrompt,
        string model,
        int contextWindowSize,
        IReadOnlyList<string>? tools = null,
        bool isDirectMessage = false,
        int? maxIterations = null)
    {
        var skillsPaths = _workspaceService.GetSkillsPaths();
        // KC-5003: DM sessions use workspace root as sandbox — same trust boundary as the main session.
        // Group sessions remain isolated to their sessionDirectory.
        var workingDirectory = isDirectMessage ? _workspaceService.RootPath : sessionDirectory;
        return new AgentConfig
        {
            Model = model,
            SystemPrompt = systemPrompt,
            MaxIterations = maxIterations ?? _options.MaxIterations,
            Tools = tools ?? _options.Tools,
            Permissions = _options.Permissions,
            SandboxOptions = new SandboxOptions
            {
                WorkingDirectory = workingDirectory,
                EnforceBoundary = true,
                AllowPaths = skillsPaths,
            },
            Skills = new SkillsConfig
            {
                Paths = skillsPaths,
                ValidateOnLoad = false,
                AutoActivate = BuiltinSkills.ChannelAutoActivate,
            },
            Context = new ContextManagerOptions
            {
                MaxTokens = (int)(contextWindowSize * _options.ContextCompressionTriggerRatio),
                CompressToTokens = (int)(contextWindowSize * _options.ContextCompressionTargetRatio),
                CompressionPrompt = isDirectMessage
                    ? _options.DmCompressionPrompt
                    : _options.GroupCompressionPrompt,
                ToolResultCompression = new ToolResultCompressionOptions { Enabled = true },
            },
        };
    }

    private Task<string> ResolveConfiguredModelAsync(CancellationToken cancellationToken) =>
        RuntimeProviderSelector.ResolveModelOrFallbackAsync(
            _runtimeConfigurationResolver,
            _options.Model,
            _accountRepository,
            cancellationToken);

    private async Task<int> ResolvePromptCharacterBudgetAsync(CancellationToken cancellationToken)
    {
        if (_accountRepository is null) return _options.MaxPromptCharacters;
        try
        {
            var resolved = await _accountRepository.ResolveDefaultForAsync(
                ModelCapabilitySet.Text, cancellationToken);
            if (resolved is null) return _options.MaxPromptCharacters;
            var maxOutputCap = Math.Min(resolved.Model.MaxOutputTokens, Math.Min((int)(resolved.Model.ContextWindowSize * 0.20), 16_384));
            var usableTokens = Math.Max(resolved.Model.ContextWindowSize - maxOutputCap, 0);
            return Math.Max(usableTokens / 5 * 4, _options.MaxPromptCharacters);
        }
        catch
        {
            return _options.MaxPromptCharacters;
        }
    }

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

    private PromptBuildResult BuildSystemPrompt(
        ThreadBinding binding,
        ChannelPolicy policy,
        IReadOnlyList<PromptContextDocument> contextDocuments,
        int promptCharBudget)
    {
        var scope = ResolveEffectiveScope(policy);
        var displayTitle = binding.ChannelIdentity.DisplayName
            ?? binding.ChannelIdentity.Username
            ?? binding.ExternalThreadId;

        var sessionStartedAt = DateTimeOffset.Now;
        var builder = new PromptBuilder(PromptProfiles.Channel(binding.ThreadType, _options.SystemPrompt))
            .WithCharacterBudget(promptCharBudget)
            .AddBody($"Session started at: {sessionStartedAt:yyyy-MM-dd HH:mm:ss zzz} ({sessionStartedAt.DayOfWeek}).")
            .AddSection("Runtime Environment", RuntimeEnvironmentContext.BuildLines(_workspaceService.RootPath))
            .AddSection(
                "Channel Session",
                [
                    $"BindingId: {binding.Id}",
                    $"ConnectorKind: {binding.ConnectorKind}",
                    $"AccountId: {binding.AccountId}",
                    $"ExternalThreadId: {binding.ExternalThreadId}",
                    $"ThreadType: {binding.ThreadType}",
                    $"SessionKind: {binding.SessionKind}",
                    $"DisplayTitle: {displayTitle}",
                ])
            .AddSection(
                "Policy",
                [
                    $"- AllowDirectReply: {policy.AllowDirectReply}",
                    $"- RequireExplicitMention: {policy.RequireExplicitMention}",
                    $"- WorkspaceMuted: {policy.WorkspaceMuted}",
                    $"- ConnectorMuted: {policy.ConnectorMuted}",
                    $"- ThreadMuted: {policy.ThreadMuted}",
                    $"- LoadAgents: {scope.LoadAgents}",
                    $"- LoadIdentity: {scope.LoadIdentity}",
                    $"- LoadSoul: {scope.LoadSoul}",
                    $"- LoadUserProfile: {scope.LoadUserProfile}",
                    $"- LoadLongTermMemory: {scope.LoadLongTermMemory}",
                    $"- LoadRecentThreadSummary: {scope.LoadRecentThreadSummary}",
                ])
            .AddBody("Only use the loaded context files below. Do not assume access to main-session memory or undeclared user profile data.");

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

        var prompt = builder.AddContextDocuments(contextDocuments).Build();

        return prompt;
    }

    private static string BuildInboundTurnPrompt(
        ThreadBinding binding,
        ChannelEventEnvelope envelope,
        bool hasExplicitMention)
    {
        var senderLabel = envelope.Sender?.DisplayName
            ?? envelope.Sender?.Username
            ?? envelope.Sender?.Id
            ?? "(unknown)";
        var recipientLabel = envelope.Recipient?.DisplayName
            ?? envelope.Recipient?.Username
            ?? envelope.Recipient?.Id
            ?? "(unknown)";
        var messageText = string.IsNullOrWhiteSpace(envelope.Text)
            ? "(no text)"
            : envelope.Text.Trim();
        var mediaInfo = FormatMediaAttachments(envelope.MediaAttachments);
        var threadGuidance = binding.ThreadType switch
        {
            ChannelThreadType.DirectMessage => """
- This is a direct message. Process the user's request using available tools, then use channel_send to reply.
- Use the loaded user profile only when it is explicitly present in the session context.
""",
            ChannelThreadType.Group when hasExplicitMention => """
- This is a group thread and Koda was explicitly mentioned.
- Process the request using available tools, then use channel_send if a reply is useful.
- Keep the reply brief, public-safe, and grounded only in the loaded context.
""",
            ChannelThreadType.Group => """
- This is a group thread without an explicit mention of Koda.
- Do not send a reply unless the message clearly requires Koda's intervention.
""",
            _ => string.Empty,
        };

        return $$"""
Process the inbound channel event below. You are in Full Agent Mode — you can use all available tools.

{{threadGuidance}}
To send a reply, use the channel_send tool with:
  bindingId: {{binding.Id}}
  text: <your reply text>

Use available tools (fs_read, fs_list, bash_run, etc.) before replying if needed to answer the request.
Do not output the reply as plain text — always use channel_send to deliver it.

Inbound Event:
- BindingId: {{binding.Id}}
- EventType: {{envelope.EventType}}
- ThreadType: {{binding.ThreadType}}
- ExternalMessageId: {{envelope.ExternalMessageId ?? "(none)"}}
- Sender: {{senderLabel}}
- Recipient: {{recipientLabel}}
- HasExplicitMention: {{hasExplicitMention}}
- MessageText:
{{messageText}}
{{mediaInfo}}
""";
    }

    private static string FormatMediaAttachments(IReadOnlyList<MediaReference>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("- MediaAttachments:");
        foreach (var attachment in attachments)
        {
            var sizeKb = attachment.SizeBytes / 1024.0;
            sb.AppendLine($"  - [{attachment.ContentType}] {attachment.FileName} ({sizeKb:F1}KB) MediaId={attachment.MediaId}");
        }

        return sb.ToString();
    }

    private static void ValidateBindingAndPolicy(ThreadBinding binding, ChannelPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(binding.Id))
        {
            throw new ArgumentException("Channel binding id is required.", nameof(binding));
        }

        if (string.IsNullOrWhiteSpace(binding.SessionId))
        {
            throw new ArgumentException("Channel binding session id is required.", nameof(binding));
        }

        var expectedSessionKind = binding.ThreadType switch
        {
            ChannelThreadType.DirectMessage => SessionKind.ChannelDirectMessage,
            ChannelThreadType.Group => SessionKind.ChannelGroup,
            _ => throw new ArgumentOutOfRangeException(nameof(binding), binding.ThreadType, null),
        };

        if (binding.SessionKind != expectedSessionKind)
        {
            throw new ArgumentException(
                $"Binding session kind '{binding.SessionKind}' does not match thread type '{binding.ThreadType}'.",
                nameof(binding));
        }

        if (policy.ThreadType != binding.ThreadType)
        {
            throw new ArgumentException(
                $"Channel policy thread type '{policy.ThreadType}' does not match binding thread type '{binding.ThreadType}'.",
                nameof(policy));
        }
    }

    internal static EffectivePolicyScope ResolveEffectiveScope(ChannelPolicy policy)
    {
        var isDirectMessage = policy.ThreadType == ChannelThreadType.DirectMessage;

        // KC-5001/5002/5003: DM sessions are owner-only and trust-equivalent to the main session.
        // They load full workspace context (including long-term memory and daily memory),
        // have no session timeout, and can write back to workspace files.
        // Group sessions remain conservative — external members are not trusted.
        return new EffectivePolicyScope(
            LoadAgents: policy.LoadAgents,
            LoadIdentity: policy.LoadIdentity,
            LoadSoul: policy.LoadSoul,
            LoadUserProfile: isDirectMessage && policy.LoadUserProfile,
            LoadLongTermMemory: isDirectMessage,
            LoadRecentThreadSummary: policy.LoadRecentThreadSummary);
    }

    private static string ToDisplayPath(string workspaceRoot, string absolutePath)
    {
        var relativePath = Path.GetRelativePath(workspaceRoot, absolutePath);
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string FormatResumeFailureMessage(Exception exception)
    {
        var root = exception.GetBaseException();
        return $"Resume failed: {root.GetType().Name}: {root.Message}";
    }

    private static StringComparer GetPathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private static ChannelSessionHandle CreateHandle(
        ThreadBinding binding,
        string sessionDirectory,
        bool resumedFromStore,
        string? resumeFailureMessage,
        IAgent agent)
    {
        return new ChannelSessionHandle(
            BindingId: binding.Id,
            SessionId: binding.SessionId,
            SessionKind: binding.SessionKind,
            SessionDirectory: sessionDirectory,
            ResumedFromStore: resumedFromStore,
            ResumeFailureMessage: resumeFailureMessage,
            Agent: agent);
    }
    private void RecordDiagnosticEvent(string eventType, string level, string message, string sessionId)
    {
        if (_diagnosticsService == null) return;
        _diagnosticsService.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: DiagnosticSource,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: sessionId));
    }

    internal sealed record EffectivePolicyScope(
        bool LoadAgents,
        bool LoadIdentity,
        bool LoadSoul,
        bool LoadUserProfile,
        bool LoadLongTermMemory,
        bool LoadRecentThreadSummary);

    private sealed class SemaphoreReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose()
        {
            try { semaphore.Release(); }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }
    }
}
