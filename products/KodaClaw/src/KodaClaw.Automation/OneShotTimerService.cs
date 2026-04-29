using System.Text.Json;
using KodaClaw.Automation.Scheduler;
using Kode.Agent.Sdk.Core.Abstractions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Timers;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Automation;

public sealed class OneShotTimerService : IOneShotTimerService
{
    private const string TimerSource = "oneshot.timer";

    private readonly IOneShotTimerRepository _timerRepository;
    private readonly IAutomationSessionService _sessionService;
    private readonly IInboxRepository _inboxRepository;
    private readonly IAutomationClock _clock;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly ILogger<OneShotTimerService>? _logger;

    public OneShotTimerService(
        IOneShotTimerRepository timerRepository,
        IAutomationSessionService sessionService,
        IInboxRepository inboxRepository,
        IAutomationClock clock,
        IDiagnosticsService? diagnosticsService = null,
        ILogger<OneShotTimerService>? logger = null)
    {
        _timerRepository = timerRepository ?? throw new ArgumentNullException(nameof(timerRepository));
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _inboxRepository = inboxRepository ?? throw new ArgumentNullException(nameof(inboxRepository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _diagnosticsService = diagnosticsService;
        _logger = logger;
    }

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var pending = await _timerRepository.ListPendingAsync(now, cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var timer in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await FireTimerAsync(timer, cancellationToken);
        }
    }

    private async Task FireTimerAsync(OneShotTimerRecord timer, CancellationToken cancellationToken)
    {
        // Claim the timer slot immediately to prevent double-fire on concurrent ticks.
        var claimedTimer = timer with { Status = OneShotTimerStatus.Fired, FiredAt = _clock.UtcNow };
        var claimed = await _timerRepository.UpdateAsync(claimedTimer, cancellationToken);
        if (!claimed)
        {
            // Already updated by another process; skip.
            return;
        }

        var definition = BuildSyntheticDefinition(timer);
        await RunTimerSessionAsync(timer, definition, cancellationToken);
    }

    private async Task RunTimerSessionAsync(
        OneShotTimerRecord timer,
        AutomationDefinition definition,
        CancellationToken cancellationToken)
    {
        AutomationSessionHandle? handle = null;
        var agentDisposed = false;
        try
        {
            handle = await _sessionService.StartAutomationSessionAsync(definition, cancellationToken);

            AgentRunResult runResult;
            try
            {
                runResult = await handle.Agent.RunAsync("Run the scheduled reminder now.", cancellationToken);
            }
            finally
            {
                await handle.Agent.DisposeAsync();
                agentDisposed = true;
            }

            if (runResult.Success)
            {
                var successTimer = timer with
                {
                    Status = OneShotTimerStatus.Fired,
                    FiredAt = _clock.UtcNow,
                    ErrorMessage = null,
                };
                await _timerRepository.UpdateAsync(successTimer, cancellationToken);
                await WriteInboxItemAsync(timer, handle.SessionId, runResult.Response, errorMessage: null, cancellationToken);
                RecordDiagnosticEvent("oneshot_timer.fired", "info",
                    $"One-shot timer fired: id={timer.Id} title={timer.Title}");
            }
            else
            {
                var errorMessage = $"Timer session did not complete (stop reason: {runResult.StopReason}).";
                var failedTimer = timer with
                {
                    Status = OneShotTimerStatus.Fired,
                    FiredAt = _clock.UtcNow,
                    ErrorMessage = errorMessage,
                };
                await _timerRepository.UpdateAsync(failedTimer, cancellationToken);
                await WriteInboxItemAsync(timer, handle.SessionId, runResult.Response, errorMessage, cancellationToken);
                RecordDiagnosticEvent("oneshot_timer.fire_failed", "warning",
                    $"One-shot timer session failed: id={timer.Id} stopReason={runResult.StopReason}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "One-shot timer session crashed: id={TimerId}.", timer.Id);
            var errorMessage = ex.GetBaseException().Message;
            var failedTimer = timer with
            {
                Status = OneShotTimerStatus.Fired,
                FiredAt = _clock.UtcNow,
                ErrorMessage = errorMessage,
            };
            await _timerRepository.UpdateAsync(failedTimer, CancellationToken.None);
            await WriteInboxItemAsync(timer, sessionId: null, summary: null, errorMessage, CancellationToken.None);
            RecordDiagnosticEvent("oneshot_timer.fire_crashed", "error",
                $"One-shot timer session crashed: id={timer.Id} error={errorMessage}");

            if (!agentDisposed && handle is { } danglingHandle)
            {
                await danglingHandle.Agent.DisposeAsync();
            }
        }
    }

    private async Task WriteInboxItemAsync(
        OneShotTimerRecord timer,
        string? sessionId,
        string? summary,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var succeeded = errorMessage is null;
        var inboxId = $"oneshot-timer-{timer.Id}";
        var title = string.IsNullOrWhiteSpace(timer.Title)
            ? (succeeded ? "提醒已触发" : "提醒触发失败")
            : timer.Title;
        var displaySummary = summary?.Trim()
                             ?? (succeeded ? "定时提醒已执行完成。" : "定时提醒执行失败。");

        var payload = JsonSerializer.Serialize(new
        {
            timerId = timer.Id,
            title = timer.Title,
            prompt = timer.Prompt,
            fireAt = timer.FireAt,
            firedAt = timer.FiredAt,
            succeeded,
            summary = displaySummary,
            errorMessage = errorMessage,
            channels = timer.Channels,
        });

        var item = new InboxItem(
            Id: inboxId,
            Kind: InboxItemKind.AutomationResult,
            Status: InboxItemStatus.Open,
            Title: title,
            Summary: displaySummary,
            Source: TimerSource,
            CreatedAt: now,
            UpdatedAt: now,
            RequiresAction: !succeeded,
            Route: "/automations",
            SessionId: sessionId,
            CorrelationId: timer.Id,
            ApprovalId: null,
            PayloadJson: payload,
            ResolvedAt: null);

        await _inboxRepository.UpsertAsync(item, cancellationToken);
    }

    private static AutomationDefinition BuildSyntheticDefinition(OneShotTimerRecord timer)
    {
        var now = timer.FireAt;
        return new AutomationDefinition(
            Id: $"oneshot-{timer.Id}",
            Title: timer.Title ?? "一次性提醒",
            Prompt: timer.Prompt,
            Source: AutomationDefinitionSource.OneShot,
            SourcePath: null,
            CronExpression: "0 * * * *",
            Enabled: true,
            InputPaths: null,
            ModelId: null,
            NotificationChannels: timer.Channels,
            NotifyMode: timer.Channels is { Count: > 0 }
                ? AutomationNotifyMode.Auto
                : AutomationNotifyMode.None,
            CreatedAt: timer.CreatedAt,
            UpdatedAt: now,
            LastRunAt: null,
            NextRunAt: null,
            LastRunStatus: null,
            LastError: null);
    }

    private void RecordDiagnosticEvent(string eventType, string level, string message)
    {
        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: TimerSource,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: null));
    }
}
