using System.Collections.Concurrent;
using System.Text.Json;
using KodaClaw.ChannelHub.Commands;
using KodaClaw.ChannelHub.Common;
using KodaClaw.ChannelHub.Delivery;
using KodaClaw.ChannelHub.Inbound;
using KodaClaw.ChannelHub.Policy;
using KodaClaw.ChannelHub.Send;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Microsoft.Extensions.Logging;

namespace KodaClaw.ChannelHub.Turn;

public sealed class ChannelTurnOrchestrator
{
    private const string TurnSource = "channel.turn";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Fixed by Nietzsche: persistent dedup to survive gateway restarts.
    // Feishu WS uses at-least-once delivery; on reconnect it re-pushes unacknowledged
    // messages with a new event_id but the same message_id. Without persistent dedup,
    // gateway restart clears the in-memory set and duplicate turns fire.
    // key = "{connectorKind}::{accountId}::{externalMessageId}"，value = 首次处理时间
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentMessageIds = new(StringComparer.Ordinal);
    private static readonly TimeSpan MessageDeduplicationWindow = TimeSpan.FromMinutes(30);
    private const string DedupeFileName = ".dedupe-state.json";

    private readonly ChannelEventIngestionService _ingestionService;
    private readonly IChannelSessionService _channelSessionService;
    private readonly ChannelPolicyEngine _policyEngine;
    private readonly IChannelAccountRepository _channelAccountRepository;
    private readonly ChannelDeliveryGovernanceService _deliveryGovernanceService;
    private readonly ChannelDeliveryDispatchService _deliveryDispatchService;
    private readonly ChannelConnectorKindResolver? _connectorResolver;
    private readonly IApprovalRepository? _approvalRepository;
    private readonly ChannelDeliveryApprovalService? _deliveryApprovalService;
    private readonly IChannelAuditRepository? _channelAuditRepository;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly ICorrelationContextAccessor? _correlationContextAccessor;
    private readonly IChannelThreadSummaryWriter? _summaryWriter;
    private readonly IChannelSendCapture? _sendCapture;
    private readonly IModelProvider? _modelProvider;
    private readonly ChannelCommandDispatcher? _commandDispatcher;
    private readonly IChannelSessionStatsTracker? _statsTracker;
    private readonly ChannelSessionOptions _sessionOptions;
    private readonly ILogger<ChannelTurnOrchestrator> _logger;
    private readonly string? _dedupeFilePath;

    public ChannelTurnOrchestrator(
        ChannelEventIngestionService ingestionService,
        IChannelSessionService channelSessionService,
        ChannelPolicyEngine policyEngine,
        IChannelAccountRepository channelAccountRepository,
        ChannelDeliveryGovernanceService deliveryGovernanceService,
        ChannelDeliveryDispatchService deliveryDispatchService,
        IApprovalRepository? approvalRepository = null,
        ChannelDeliveryApprovalService? deliveryApprovalService = null,
        IChannelAuditRepository? channelAuditRepository = null,
        IDiagnosticsService? diagnosticsService = null,
        ICorrelationContextAccessor? correlationContextAccessor = null,
        IChannelThreadSummaryWriter? summaryWriter = null,
        IChannelSendCapture? sendCapture = null,
        IModelProvider? modelProvider = null,
        ChannelCommandDispatcher? commandDispatcher = null,
        IChannelSessionStatsTracker? statsTracker = null,
        ChannelSessionOptions? sessionOptions = null,
        KodaClawWorkspaceOptions? workspaceOptions = null,
        ChannelConnectorKindResolver? connectorResolver = null,
        ILogger<ChannelTurnOrchestrator>? logger = null)
    {
        _ingestionService = ingestionService ?? throw new ArgumentNullException(nameof(ingestionService));
        _channelSessionService = channelSessionService ?? throw new ArgumentNullException(nameof(channelSessionService));
        _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));
        _channelAccountRepository = channelAccountRepository ?? throw new ArgumentNullException(nameof(channelAccountRepository));
        _deliveryGovernanceService = deliveryGovernanceService ?? throw new ArgumentNullException(nameof(deliveryGovernanceService));
        _deliveryDispatchService = deliveryDispatchService ?? throw new ArgumentNullException(nameof(deliveryDispatchService));
        _approvalRepository = approvalRepository;
        _deliveryApprovalService = deliveryApprovalService;
        _channelAuditRepository = channelAuditRepository;
        _diagnosticsService = diagnosticsService;
        _correlationContextAccessor = correlationContextAccessor;
        _summaryWriter = summaryWriter;
        _sendCapture = sendCapture;
        _modelProvider = modelProvider;
        _commandDispatcher = commandDispatcher;
        _statsTracker = statsTracker;
        _sessionOptions = sessionOptions ?? new ChannelSessionOptions();
        _connectorResolver = connectorResolver;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ChannelTurnOrchestrator>.Instance;
        var workspaceRoot = workspaceOptions?.ResolveRootPath();
        _dedupeFilePath = !string.IsNullOrWhiteSpace(workspaceRoot)
            ? Path.Combine(workspaceRoot, DedupeFileName)
            : null;
        LoadDedupeState();
    }

    public async Task<ChannelTurnOrchestrationResult> ProcessInboundAsync(
        ChannelEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var processing = await _ingestionService.IngestAsync(envelope, cancellationToken);
        var account = await _channelAccountRepository.GetByIdAsync(processing.Binding.AccountId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Channel account '{processing.Binding.AccountId}' was not found.");

        // Pre-check: if the message looks like an approval response and there are pending
        // channel delivery approvals for this thread, handle the decision without running
        // an agent turn.
        if (_approvalRepository is not null && _deliveryApprovalService is not null)
        {
            var responseIntent = ChannelApprovalResponseParser.TryParse(envelope.Text);
            if (responseIntent is not null)
            {
                var (match, hasMultiplePending) = await TryFindPendingApprovalAsync(
                    processing.Binding.Id, responseIntent.Token, cancellationToken);

                if (match is not null)
                {
                    return await HandleChannelApprovalResponseAsync(
                        processing, account, match, responseIntent, envelope, cancellationToken);
                }

                if (hasMultiplePending)
                {
                    var hint = "多个草稿待审批，请带编号（如 ok A3F9C1）。";
                    try
                    {
                        await _deliveryDispatchService.SendNotificationAsync(account, processing.Binding, hint, cancellationToken: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send approval ambiguity hint for binding {BindingId}", processing.Binding.Id);
                    }

                    var hintOutcome = CreateOutcome(
                        ChannelTurnOutcomeKind.NoAction,
                        hint,
                        processing,
                        envelope,
                        reasonCode: "approval_response_ambiguous");
                    return new ChannelTurnOrchestrationResult(processing, hintOutcome, ExecutedTurn: false);
                }

                // No pending approvals for this binding; fall through to normal agent turn.
            }
        }

        // KC-CMD: Route all slash commands through the unified command parser + dispatcher.
        var parsedCommand = ChannelCommandParser.Parse(envelope.Text);
        if (parsedCommand.ControlKind.HasValue && _commandDispatcher is not null)
        {
            var commandHandled = await _commandDispatcher.DispatchAsync(
                parsedCommand,
                processing.Binding.SessionId,
                account,
                processing.Binding,
                cancellationToken);

            if (commandHandled)
            {
                var reasonCode = parsedCommand.ControlKind switch
                {
                    ChannelControlCommandKind.NewSession => "session_reset_command",
                    ChannelControlCommandKind.Status => "status_command",
                    ChannelControlCommandKind.Stop => "stop_command",
                    ChannelControlCommandKind.Help => "help_command",
                    _ => "control_command",
                };
                var cmdOutcome = CreateOutcome(
                    ChannelTurnOutcomeKind.NoAction,
                    $"Control command handled: {parsedCommand.ControlKind}",
                    processing,
                    envelope,
                    reasonCode: reasonCode);
                RecordDiagnosticEvent($"channel.turn.{reasonCode}", "info", reasonCode, processing.Binding, cmdOutcome);
                return new ChannelTurnOrchestrationResult(processing, cmdOutcome, ExecutedTurn: false);
            }
        }
        else if (parsedCommand.ControlKind.HasValue && _commandDispatcher is null)
        {
            // Fallback for backwards compatibility when no dispatcher is injected (legacy path)
            var trimmedText = envelope.Text?.Trim() ?? "";
            if (IsSessionResetCommand(trimmedText))
            {
                await _channelSessionService.RotateSessionAsync(processing.Binding, cancellationToken: cancellationToken);
                const string resetConfirmation = "已开启新会话。";
                try
                {
                    await _deliveryDispatchService.SendNotificationAsync(
                        account, processing.Binding, resetConfirmation, cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send session reset confirmation for binding {BindingId}", processing.Binding.Id);
                }

                var resetOutcome = CreateOutcome(
                    ChannelTurnOutcomeKind.NoAction,
                    resetConfirmation,
                    processing,
                    envelope,
                    reasonCode: "session_reset_command");
                RecordDiagnosticEvent("channel.turn.session_reset", "info", resetConfirmation, processing.Binding, resetOutcome);
                return new ChannelTurnOrchestrationResult(processing, resetOutcome, ExecutedTurn: false);
            }
        }

        if (!ShouldExecuteTurn(envelope))
        {
            var outcome = CreateOutcome(
                ChannelTurnOutcomeKind.NoAction,
                summary: "Inbound event recorded without a reply turn.",
                processing,
                envelope,
                reasonCode: "event_not_eligible");
            await AppendOutcomeAuditAsync(processing.Binding, processing.DeliveryRule.Mode, "turn.no_action", outcome, cancellationToken);
            RecordDiagnosticEvent("channel.turn.no_action", "info", outcome.Summary, processing.Binding, outcome);
            return new ChannelTurnOrchestrationResult(processing, outcome, ExecutedTurn: false);
        }

        // 短时间窗口去重：同一 ExternalMessageId 在 60 秒内只处理一次，
        // 防止 iLink 等渠道的 at-least-once 重复投递触发多次 Agent turn。
        if (!string.IsNullOrEmpty(envelope.ExternalMessageId))
        {
            var dedupKey = $"{envelope.ConnectorKind}::{envelope.AccountId}::{envelope.ExternalMessageId}";
            var now = DateTimeOffset.UtcNow;

            // 懒清理：移除超过去重窗口的旧记录
            foreach (var stale in _recentMessageIds
                .Where(kv => now - kv.Value > MessageDeduplicationWindow)
                .Select(kv => kv.Key)
                .ToList())
            {
                _recentMessageIds.TryRemove(stale, out _);
            }

            if (!_recentMessageIds.TryAdd(dedupKey, now))
            {
                var dupOutcome = CreateOutcome(
                    ChannelTurnOutcomeKind.NoAction,
                    summary: $"Duplicate message suppressed (externalMessageId={envelope.ExternalMessageId}).",
                    processing,
                    envelope,
                    reasonCode: "duplicate_message");
                _logger.LogInformation(
                    "Suppressed duplicate channel turn for binding {BindingId} externalMessageId={ExternalMessageId}",
                    processing.Binding.Id, envelope.ExternalMessageId);
                RecordDiagnosticEvent("channel.turn.duplicate_suppressed", "info", dupOutcome.Summary, processing.Binding, dupOutcome);
                return new ChannelTurnOrchestrationResult(processing, dupOutcome, ExecutedTurn: false);
            }

            SaveDedupeState();
        }

        var hasExplicitMention = DetectExplicitMention(envelope, account);

        // Build turn context by layering per-turn directives (/think <msg>, /focus) on top of
        // the sticky toggles persisted on the binding (/think on, /stream on).
        var turnContext = ChannelTurnContext.FromDirectivesAndBinding(parsedCommand, processing.Binding);

        // Guard: directive present but no actual prompt body — inform the user.
        if (parsedCommand.Directives.Count > 0 && string.IsNullOrWhiteSpace(parsedCommand.CleanedText))
        {
            var directiveNames = string.Join(", ", parsedCommand.Directives.Select(d => $"/{d.ToString().ToLowerInvariant()}"));
            var usageHint = $"请在指令 {directiveNames} 后加上消息正文，例如：/{parsedCommand.Directives[0].ToString().ToLowerInvariant()} 你的问题";
            try
            {
                await _deliveryDispatchService.SendNotificationAsync(
                    account, processing.Binding, usageHint, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send directive usage hint for binding {BindingId}", processing.Binding.Id);
            }
            var hintOutcome = CreateOutcome(
                ChannelTurnOutcomeKind.NoAction,
                usageHint,
                processing,
                envelope,
                reasonCode: "directive_missing_body");
            return new ChannelTurnOrchestrationResult(processing, hintOutcome, ExecutedTurn: false);
        }

        try
        {
            var handle = await _channelSessionService.EnsureChannelSessionAsync(
                processing.Binding, processing.Policy, cancellationToken);

            // Build the effective prompt, applying directive context
            var effectiveEnvelope = parsedCommand.Directives.Count > 0
                ? envelope with { Text = parsedCommand.CleanedText }
                : envelope;
            var prompt = _channelSessionService.BuildPrompt(
                processing.Binding, effectiveEnvelope, hasExplicitMention);

            // Apply FocusConstraint as a trailing instruction
            if (!string.IsNullOrWhiteSpace(turnContext.FocusConstraint))
                prompt = $"{prompt}\n\n[本轮约束：请专注于 {turnContext.FocusConstraint}]";

            // Apply PromptPrefix (Wave 3+)
            if (!string.IsNullOrWhiteSpace(turnContext.PromptPrefix))
                prompt = $"{turnContext.PromptPrefix}\n\n{prompt}";

            using var sessionLock = await _channelSessionService.AcquireSessionLockAsync(
                handle.SessionId, cancellationToken);

            AgentRunResult runResult;
            var progressWasSent = false;

            // KC-7203: Decide between ChannelProgressIndicator (edit-in-place) and
            // ChannelProgressStreamer (intermediate-text delivery). Mutex — at most one is activated.
            var (indicatorEnabled, indicatorStyle) = TryReadProgressIndicatorSetting(account);
            var connectorSupportsEdit = false;
            if (_connectorResolver is not null
                && _connectorResolver.TryGet(account.ConnectorKind, out var resolvedConnector)
                && resolvedConnector is not null)
            {
                connectorSupportsEdit = resolvedConnector.SupportsEdit;
            }
            var useIndicator = indicatorEnabled && connectorSupportsEdit;

            // Streamer still gets per-turn override priority over session option,
            // but only matters when the indicator path is NOT chosen.
            var effectiveProgressStreaming = turnContext.EnableProgressStreamingOverride ?? _sessionOptions.EnableProgressStreaming;

            CancellationTokenSource? subscribeCts = null;
            Task<bool>? progressTask = null;
            Task<ChannelProgressIndicatorResult>? indicatorTask = null;
            var turnStartedAt = DateTimeOffset.UtcNow;

            if (useIndicator)
            {
                subscribeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var indicatorEvents = handle.Agent.EventBus.SubscribeAsync(
                    EventChannel.Progress | EventChannel.Monitor,
                    since: null,
                    kinds: ["breakpoint_changed", "tool:start", "tool:end", "done"],
                    cancellationToken: subscribeCts.Token);

                var agent = handle.Agent;
                // agent.StepCount 是 session 累计值，用户视角的"第 N 步"应是本 turn 相对值。
                var stepBaseline = agent.StepCount;
                indicatorTask = ChannelProgressIndicator.RunAsync(
                    events: indicatorEvents,
                    sendInitial: (text, ct) => _deliveryDispatchService.SendProgressInitialAsync(
                        account, processing.Binding, text, ct),
                    edit: (msgId, text, ct) => _deliveryDispatchService.EditProgressAsync(
                        account, processing.Binding, msgId, text, ct),
                    getStepCount: () => Math.Max(1, agent.StepCount - stepBaseline),
                    turnStartedAt: turnStartedAt,
                    options: new ChannelProgressIndicator.Options(Style: indicatorStyle),
                    cancellationToken: subscribeCts.Token);
            }
            else if (effectiveProgressStreaming)
            {
                subscribeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var progressEvents = handle.Agent.EventBus.SubscribeAsync(
                    EventChannel.Progress,
                    since: null,
                    kinds: ["text_chunk_end", "tool:start", "done"],
                    cancellationToken: subscribeCts.Token);

                progressTask = ChannelProgressStreamer.StreamAsync(
                    progressEvents,
                    (text, ct) =>
                        _deliveryDispatchService.SendNotificationAsync(
                            account, processing.Binding, text, cancellationToken: ct),
                    subscribeCts.Token);
            }

            // Build AgentRunOptions from turn context directives
            var runOptions = (turnContext.EnableThinking.HasValue || turnContext.ThinkingBudget.HasValue)
                ? new Kode.Agent.Sdk.Core.Abstractions.AgentRunOptions
                  {
                      EnableThinking = turnContext.EnableThinking,
                      ThinkingBudget = turnContext.ThinkingBudget
                  }
                : null;

            try
            {
                runResult = runOptions is not null
                    ? await handle.Agent.RunAsync(prompt, runOptions, cancellationToken)
                    : await handle.Agent.RunAsync(prompt, cancellationToken);
            }
            finally
            {
                if (subscribeCts is not null)
                {
                    if (indicatorTask is not null)
                    {
                        // Indicator 必须消费到 DoneEvent 才能写入 ✓ 完成 / ✗ 已取消 终态。
                        // Agent 发送顺序为 BreakpointChanged(Ready) → DoneEvent，若这里立即 Cancel，
                        // 订阅队列里的 DoneEvent 会被丢弃，终态定格在 "🔄 思考中…"。
                        // 给一个短宽限期让 indicator 自然返回；超时或外部取消才兜底 Cancel。
                        var graceTask = Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                        Task completed;
                        try
                        {
                            completed = await Task.WhenAny(indicatorTask, graceTask).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            completed = graceTask;
                        }

                        if (completed != indicatorTask)
                        {
                            subscribeCts.Cancel();
                        }

                        try
                        {
                            var indicatorResult = await indicatorTask.ConfigureAwait(false);
                            progressWasSent = indicatorResult.ReachedDone && !indicatorResult.Degraded;
                            RecordIndicatorDiagnostic(indicatorResult, processing.Binding);
                        }
                        catch (OperationCanceledException) { }
                    }
                    else if (progressTask is not null)
                    {
                        subscribeCts.Cancel();
                        try { progressWasSent = await progressTask; }
                        catch (OperationCanceledException) { progressWasSent = false; }
                    }
                    else
                    {
                        subscribeCts.Cancel();
                    }
                }
            }

            _statsTracker?.Record(processing.Binding.SessionId, runResult.TokenUsage);

            var execution = new ChannelTurnExecutionResult(
                Session: handle,
                RunResult: runResult,
                RawResponse: runResult.Response ?? string.Empty,
                Proposal: null,
                HasExplicitMention: hasExplicitMention);

            if (execution.RunResult.StopReason == StopReason.MaxIterations)
            {
                const string maxIterationsNotification = "本次对话轮次已达上限，如需继续请新发消息。";
                try
                {
                    await _deliveryDispatchService.SendNotificationAsync(
                        account, processing.Binding, maxIterationsNotification, cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to send MaxIterations notification for binding {BindingId}", processing.Binding.Id);
                }
            }

            var sentTexts = _sendCapture?.GetAndClear(processing.Binding.Id) ?? [];

            // Fallback：Agent 没有调用 channel_send 时，自动投递最终文本。
            _logger.LogDebug(
                "Channel turn fallback check: sentTexts={Count} progressWasSent={Progress} rawResponse={HasResponse} bindingId={BindingId}",
                sentTexts.Count,
                progressWasSent,
                !string.IsNullOrWhiteSpace(execution.RawResponse),
                processing.Binding.Id);

            var fallbackDeliveryFailed = false;
            if (sentTexts.Count == 0 && !string.IsNullOrWhiteSpace(execution.RawResponse))
            {
                try
                {
                    await _deliveryDispatchService.SendNotificationAsync(
                        account, processing.Binding, execution.RawResponse,
                        cancellationToken: cancellationToken);
                    sentTexts = [execution.RawResponse];
                }
                catch (Exception ex)
                {
                    fallbackDeliveryFailed = true;
                    _logger.LogWarning(ex,
                        "Channel turn fallback delivery failed for binding {BindingId} account {AccountId}",
                        processing.Binding.Id, processing.Binding.AccountId);
                    RecordDiagnosticEvent("channel.turn.fallback_delivery_failed", "error",
                        ex.Message, processing.Binding,
                        CreateOutcome(ChannelTurnOutcomeKind.Failed, ex.Message, processing, envelope,
                            reasonCode: "fallback_delivery_failed"));
                }
            }

            // 根因 A：agent 跑完但完全没有产出任何回复（channel_send 未调用、RawResponse 为空、无进度文本）。
            // 向用户发一条提示，避免对话无声消失。
            if (sentTexts.Count == 0 && !progressWasSent && !fallbackDeliveryFailed)
            {
                var silentFallback = execution.RunResult.StopReason == StopReason.Error
                    ? FormatChannelError(execution.RunResult.ErrorMessage)
                    : "（已完成，暂无需要回复的内容。）";
                try
                {
                    await _deliveryDispatchService.SendNotificationAsync(
                        account, processing.Binding, silentFallback, cancellationToken: cancellationToken);
                    sentTexts = [silentFallback];
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Silent fallback notification failed for binding {BindingId}", processing.Binding.Id);
                }
            }

            var summary = await BuildConversationSummaryAsync(envelope.Text, sentTexts, progressWasSent, fallbackDeliveryFailed, cancellationToken);

            var outcome = CreateOutcome(
                ChannelTurnOutcomeKind.Delivered,
                summary,
                processing, envelope,
                reasonCode: "full_agent_mode",
                hasExplicitMention: hasExplicitMention);

            await AppendOutcomeAuditAsync(processing.Binding, processing.DeliveryRule.Mode, "turn.delivered", outcome, cancellationToken);
            RecordDiagnosticEvent("channel.turn.delivered", "info", outcome.Summary, processing.Binding, outcome);
            await TryWriteThreadSummaryAsync(processing.Binding, outcome, cancellationToken);
            return new ChannelTurnOrchestrationResult(processing, outcome, ExecutedTurn: true, execution);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException or HttpRequestException)
        {
            var outcome = CreateOutcome(
                ChannelTurnOutcomeKind.Failed,
                $"Channel turn failed: {ex.Message}",
                processing,
                envelope,
                reasonCode: "turn_failed",
                hasExplicitMention: hasExplicitMention);
            await AppendOutcomeAuditAsync(processing.Binding, processing.DeliveryRule.Mode, "turn.failed", outcome, cancellationToken);
            RecordDiagnosticEvent("channel.turn.failed", "error", ex.Message, processing.Binding, outcome);
            return new ChannelTurnOrchestrationResult(processing, outcome, ExecutedTurn: true);
        }
    }

    internal static readonly HashSet<string> SessionResetCommands =
        new(["/new", "/clear", "/reset"], StringComparer.OrdinalIgnoreCase);

    internal static bool IsSessionResetCommand(string text) =>
        SessionResetCommands.Contains(text);

    internal static string FormatChannelError(string? errorMessage)
    {
        if (errorMessage == "model_empty_response")
            return "⚠️ 消息可能触发了内容安全过滤，请调整后重试。";
        if (errorMessage != null && (
                errorMessage.Contains("访问量过大") ||
                errorMessage.Contains("您的账户已达到速率限制") ||
                errorMessage.Contains("overloaded", StringComparison.OrdinalIgnoreCase) ||
                errorMessage.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                errorMessage.Contains("429") ||
                errorMessage.Contains("529")))
            return "⚠️ 模型当前访问量过大，请稍后重试。";
        if (errorMessage != null && (
                errorMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                errorMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase)))
            return "⚠️ 模型响应超时，请稍后重试。";
        if (errorMessage != null)
            return $"⚠️ 消息处理失败：{errorMessage}";
        return "⚠️ 消息处理失败，请稍后重试。";
    }

    private static bool ShouldExecuteTurn(ChannelEventEnvelope envelope)
    {
        return envelope.EventType is ChannelEventType.MessageReceived or ChannelEventType.MessageEdited
            && (!string.IsNullOrWhiteSpace(envelope.Text) || envelope.MediaAttachments is { Count: > 0 });
    }

    private static bool DetectExplicitMention(ChannelEventEnvelope envelope, ChannelAccount account)
    {
        if (string.IsNullOrWhiteSpace(envelope.Text))
        {
            return false;
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "koda",
            "kodaclaw",
        };

        AddCandidate(account.DisplayName);
        AddCandidate(account.ExternalAccountId);
        AddCandidate(envelope.Recipient?.DisplayName);
        AddCandidate(envelope.Recipient?.Username);
        AddCandidate(envelope.Recipient?.Id);

        var normalizedText = envelope.Text.Trim();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (normalizedText.Contains(candidate, StringComparison.OrdinalIgnoreCase) ||
                normalizedText.Contains($"@{candidate}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;

        void AddCandidate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            candidates.Add(value.Trim());
        }
    }

    private async Task AppendOutcomeAuditAsync(
        ThreadBinding binding,
        DeliveryMode deliveryMode,
        string eventType,
        ChannelTurnOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (_channelAuditRepository is null)
        {
            return;
        }

        await _channelAuditRepository.AppendAsync(
            new ChannelAuditEntry(
                Id: $"audit-{Guid.NewGuid():N}",
                BindingId: binding.Id,
                ConnectorKind: binding.ConnectorKind,
                AccountId: binding.AccountId,
                ExternalThreadId: binding.ExternalThreadId,
                ThreadType: binding.ThreadType,
                EventType: eventType,
                CreatedAt: outcome.OccurredAt,
                SessionId: binding.SessionId,
                ApprovalId: outcome.ApprovalId,
                DeliveryMode: deliveryMode,
                Summary: outcome.Summary,
                MetadataJson: JsonSerializer.Serialize(outcome, JsonOptions)),
            cancellationToken);
    }

    private void RecordDiagnosticEvent(
        string eventType,
        string level,
        string message,
        ThreadBinding binding,
        ChannelTurnOutcome outcome)
    {
        if (_diagnosticsService is null)
        {
            return;
        }

        _diagnosticsService.Record(new DiagnosticEvent(
            Id: $"diag-channel-turn-{Guid.NewGuid():N}",
            Source: TurnSource,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: binding.SessionId,
            CorrelationId: _correlationContextAccessor?.CorrelationId,
            Attributes: new Dictionary<string, string?>
            {
                ["bindingId"] = binding.Id,
                ["accountId"] = binding.AccountId,
                ["connectorKind"] = binding.ConnectorKind.ToString(),
                ["externalThreadId"] = binding.ExternalThreadId,
                ["outcomeKind"] = outcome.Kind.ToString(),
                ["approvalId"] = outcome.ApprovalId,
                ["inboxItemId"] = outcome.InboxItemId,
                ["draftId"] = outcome.DraftId,
            }));
    }

    private static ChannelTurnOutcome CreateOutcome(
        ChannelTurnOutcomeKind kind,
        string summary,
        ChannelInboundProcessingResult processing,
        ChannelEventEnvelope envelope,
        string? replyText = null,
        string? approvalId = null,
        string? inboxItemId = null,
        string? draftId = null,
        string? reasonCode = null,
        bool? hasExplicitMention = null)
    {
        return new ChannelTurnOutcome(
            Kind: kind,
            Summary: summary,
            OccurredAt: DateTimeOffset.UtcNow,
            ReplyText: replyText,
            DeliveryMode: processing.DeliveryRule.Mode,
            ApprovalId: approvalId,
            InboxItemId: inboxItemId,
            DraftId: draftId,
            SourceEventId: envelope.EventId,
            ReasonCode: reasonCode,
            HasExplicitMention: hasExplicitMention);
    }

    private async Task TryWriteThreadSummaryAsync(
        ThreadBinding binding,
        ChannelTurnOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (_summaryWriter is null)
        {
            return;
        }

        if (outcome.Kind is ChannelTurnOutcomeKind.Failed or ChannelTurnOutcomeKind.NoAction)
        {
            return;
        }

        try
        {
            await _summaryWriter.WriteAsync(binding, outcome, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Summary write failed for binding {BindingId} (best-effort)", binding.Id);
        }
    }

    private async Task<string> BuildConversationSummaryAsync(
        string? inboundText,
        IReadOnlyList<string> sentTexts,
        bool progressWasSent,
        bool fallbackDeliveryFailed,
        CancellationToken cancellationToken)
    {
        var user = BuildPreview(inboundText ?? "(no text)");
        if (sentTexts.Count == 0)
        {
            if (fallbackDeliveryFailed)
                return $"user: \"{user}\" → koda: (delivery failed)";
            if (progressWasSent)
                return $"user: \"{user}\" → koda: (progress only, no final reply)";
            return $"user: \"{user}\" → koda: (no reply sent)";
        }

        var koda = string.Join(" | ", sentTexts.Select(BuildPreview));
        var simple = $"user: \"{user}\" → koda: \"{koda}\"";

        if (!_sessionOptions.LlmSummaryEnabled || _modelProvider is null) return simple;

        var combinedLength = (inboundText?.Length ?? 0) + sentTexts.Sum(t => t.Length);
        if (combinedLength <= 200) return simple;

        try
        {
            var prompt = $"请用一句话（不超过50字）总结以下对话内容：\n用户：{inboundText}\nKoda：{string.Join(" ", sentTexts)}";
            var request = new ModelRequest
            {
                Model = _sessionOptions.Model,
                Messages = [Message.User(prompt)],
                MaxTokens = 200,
            };
            var response = await _modelProvider.CompleteAsync(request, cancellationToken);
            var text = response.Content.OfType<TextContent>().FirstOrDefault()?.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Length > 120 ? text[..120] : text;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM summary generation failed, falling back to simple summary");
        }

        return simple;
    }

    private static string BuildPreview(string text) => ChannelTextExtensions.Preview(text);

    // ── Channel text approval helpers ────────────────────────────────────────

    private async Task<(Approval? Match, bool HasMultiplePending)> TryFindPendingApprovalAsync(
        string bindingId,
        string? token,
        CancellationToken cancellationToken)
    {
        var pending = await _approvalRepository!.ListAsync(
            new ApprovalQuery(Status: ApprovalStatus.Pending, Kind: ApprovalKind.ChannelDelivery, Limit: 20),
            cancellationToken);

        var forBinding = pending
            .Where(a => ApprovalMatchesBinding(a, bindingId))
            .ToList();

        if (forBinding.Count == 0)
        {
            return (null, false);
        }

        if (token is not null)
        {
            var tokenMatch = forBinding.FirstOrDefault(
                a => string.Equals(ExtractTokenFromPayload(a), token, StringComparison.OrdinalIgnoreCase));
            return (tokenMatch, forBinding.Count > 1);
        }

        // No token: only auto-match when exactly one is pending.
        return forBinding.Count == 1
            ? (forBinding[0], false)
            : (null, true);
    }

    private async Task<ChannelTurnOrchestrationResult> HandleChannelApprovalResponseAsync(
        ChannelInboundProcessingResult processing,
        ChannelAccount account,
        Approval approval,
        ApprovalResponseIntent intent,
        ChannelEventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var result = intent.Action == ApprovalAction.Approve
            ? await _deliveryApprovalService!.ApproveAsync(approval, note: null, cancellationToken)
            : await _deliveryApprovalService!.RejectAsync(approval, note: null, cancellationToken);

        var confirmationText = result.Status switch
        {
            ChannelDeliveryApprovalDispatchStatus.Completed =>
                intent.Action == ApprovalAction.Approve ? "✓ 草稿已发送。" : "✗ 草稿已取消。",
            ChannelDeliveryApprovalDispatchStatus.NotPending =>
                "该草稿已处理，无需操作。",
            ChannelDeliveryApprovalDispatchStatus.DeliveryFailed =>
                "草稿批准成功，但发送失败，请稍后通过 Web 界面重试。",
            _ => "操作失败，请通过 Web 界面处理。",
        };

        try
        {
            await _deliveryDispatchService.SendNotificationAsync(
                account, processing.Binding, confirmationText, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send approval confirmation for binding {BindingId}", processing.Binding.Id);
        }

        var outcomeKind = intent.Action == ApprovalAction.Approve
            ? ChannelTurnOutcomeKind.Delivered
            : ChannelTurnOutcomeKind.NoAction;

        var outcome = CreateOutcome(
            outcomeKind,
            confirmationText,
            processing,
            envelope,
            approvalId: approval.Id,
            reasonCode: intent.Action == ApprovalAction.Approve
                ? "channel_approval_approved"
                : "channel_approval_rejected");

        RecordDiagnosticEvent(
            intent.Action == ApprovalAction.Approve
                ? "channel.turn.approval_response_approved"
                : "channel.turn.approval_response_rejected",
            "info",
            confirmationText,
            processing.Binding,
            outcome);

        return new ChannelTurnOrchestrationResult(processing, outcome, ExecutedTurn: false);
    }

    private static bool ApprovalMatchesBinding(Approval approval, string bindingId)
    {
        if (string.IsNullOrWhiteSpace(approval.PayloadJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(approval.PayloadJson);
            return doc.RootElement.TryGetProperty("bindingId", out var prop)
                && string.Equals(prop.GetString(), bindingId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractTokenFromPayload(Approval approval)
    {
        if (string.IsNullOrWhiteSpace(approval.PayloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(approval.PayloadJson);
            return doc.RootElement.TryGetProperty("token", out var prop) ? prop.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }



    /// <summary>
    /// Reads the progress indicator configuration from <see cref="ChannelAccount.ConfigurationJson"/>.
    /// Shape: <c>{ "progressIndicator": { "enabled": bool, "style": "verbose"|"compact" } }</c>.
    /// Returns <c>(false, "verbose")</c> when the key is missing or the JSON is malformed.
    /// </summary>
    private static (bool Enabled, string Style) TryReadProgressIndicatorSetting(ChannelAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.ConfigurationJson))
        {
            return (false, ChannelProgressIndicator.StyleVerbose);
        }

        try
        {
            using var doc = JsonDocument.Parse(account.ConfigurationJson);
            if (!doc.RootElement.TryGetProperty("progressIndicator", out var section)
                || section.ValueKind != JsonValueKind.Object)
            {
                return (false, ChannelProgressIndicator.StyleVerbose);
            }

            var enabled = section.TryGetProperty("enabled", out var enabledProp)
                && enabledProp.ValueKind == JsonValueKind.True;

            var style = ChannelProgressIndicator.StyleVerbose;
            if (section.TryGetProperty("style", out var styleProp)
                && styleProp.ValueKind == JsonValueKind.String)
            {
                var raw = styleProp.GetString();
                if (string.Equals(raw, ChannelProgressIndicator.StyleCompact, StringComparison.OrdinalIgnoreCase))
                {
                    style = ChannelProgressIndicator.StyleCompact;
                }
            }

            return (enabled, style);
        }
        catch (JsonException)
        {
            return (false, ChannelProgressIndicator.StyleVerbose);
        }
    }

    private void RecordIndicatorDiagnostic(ChannelProgressIndicatorResult result, ThreadBinding binding)
    {
        if (_diagnosticsService is null)
        {
            return;
        }

        var eventType = result.Degraded
            ? "channel.progress_indicator.degraded"
            : "channel.progress_indicator.completed";
        var level = result.Degraded ? "warn" : "info";
        var message = result.Degraded
            ? $"Progress indicator degraded (reason={result.FailureReason ?? "unknown"})."
            : $"Progress indicator completed with {result.EditsSucceeded} successful edits.";

        _diagnosticsService.Record(new DiagnosticEvent(
            Id: $"diag-channel-indicator-{Guid.NewGuid():N}",
            Source: TurnSource,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: binding.SessionId,
            CorrelationId: _correlationContextAccessor?.CorrelationId,
            Attributes: new Dictionary<string, string?>
            {
                ["bindingId"] = binding.Id,
                ["accountId"] = binding.AccountId,
                ["connectorKind"] = binding.ConnectorKind.ToString(),
                ["externalThreadId"] = binding.ExternalThreadId,
                ["initialSent"] = result.InitialSent.ToString(),
                ["degraded"] = result.Degraded.ToString(),
                ["reachedDone"] = result.ReachedDone.ToString(),
                ["editsAttempted"] = result.EditsAttempted.ToString(),
                ["editsSucceeded"] = result.EditsSucceeded.ToString(),
                ["externalMessageId"] = result.ExternalMessageId,
                ["failureReason"] = result.FailureReason,
            }));
    }

    // Fixed by Nietzsche: load persisted dedup state from disk on startup.
    // Re-populates the in-memory set with recently processed message IDs so
    // Feishu reconnect re-delivery is properly suppressed after a restart.
    private void LoadDedupeState()
    {
        if (_dedupeFilePath is null) return;
        try
        {
            if (!File.Exists(_dedupeFilePath)) return;

            var json = File.ReadAllText(_dedupeFilePath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (data is null) return;

            var cutoff = DateTimeOffset.UtcNow - MessageDeduplicationWindow;
            var loaded = 0;
            foreach (var kv in data)
            {
                if (DateTimeOffset.TryParse(kv.Value, out var ts) && ts > cutoff)
                {
                    _recentMessageIds.TryAdd(kv.Key, ts);
                    loaded++;
                }
            }

            if (loaded > 0)
            {
                _logger.LogInformation(
                    "Loaded {Count} dedupe entries from {FilePath} (filtered expired)", loaded, _dedupeFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load dedupe state from {FilePath}", _dedupeFilePath);
        }
    }

    // Fixed by Nietzsche: persist current dedup state to disk (fire-and-forget).
    // Writes the full in-memory set as JSON so it can be restored on restart.
    private void SaveDedupeState()
    {
        if (_dedupeFilePath is null) return;

        _ = Task.Run(() =>
        {
            try
            {
                var data = _recentMessageIds.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.ToString("O"));
                var json = JsonSerializer.Serialize(data, JsonOptions);
                File.WriteAllText(_dedupeFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save dedupe state to {FilePath}", _dedupeFilePath);
            }
        });
    }
}
