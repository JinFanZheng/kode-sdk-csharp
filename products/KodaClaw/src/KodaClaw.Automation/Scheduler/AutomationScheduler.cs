using System.Text.Json;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Settings;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Automation.Scheduler;

public sealed class AutomationScheduler : IAutomationScheduler
{
    private const string SchedulerTrigger = "automation.scheduler";
    private const string ManualTrigger = "manual";
    private const string SchedulerSource = "automation.scheduler";
    private readonly IAutomationDefinitionRepository _definitionRepository;
    private readonly IAutomationRunRepository _runRepository;
    private readonly IAutomationSessionService _sessionService;
    private readonly IInboxRepository _inboxRepository;
    private readonly IAutomationClock _clock;
    private readonly AutomationSchedulerOptions _options;
    private readonly ISettingsRepository? _settingsRepository;
    private readonly IAutomationNotificationService? _notificationService;
    private readonly IMemoryConsolidationService? _memoryConsolidationService;
    private readonly IOneShotTimerService? _oneShotTimerService;
    private readonly ICorrelationContextAccessor? _correlationContextAccessor;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly ILogger<AutomationScheduler>? _logger;
    private readonly IHostApplicationLifetime? _hostApplicationLifetime;

    public AutomationScheduler(
        IAutomationDefinitionRepository definitionRepository,
        IAutomationRunRepository runRepository,
        IAutomationSessionService sessionService,
        IInboxRepository inboxRepository,
        IAutomationClock clock,
        AutomationSchedulerOptions options,
        ISettingsRepository? settingsRepository = null,
        IAutomationNotificationService? notificationService = null,
        IMemoryConsolidationService? memoryConsolidationService = null,
        IOneShotTimerService? oneShotTimerService = null,
        ICorrelationContextAccessor? correlationContextAccessor = null,
        IDiagnosticsService? diagnosticsService = null,
        ILogger<AutomationScheduler>? logger = null,
        IHostApplicationLifetime? hostApplicationLifetime = null)
    {
        _definitionRepository = definitionRepository ?? throw new ArgumentNullException(nameof(definitionRepository));
        _runRepository = runRepository ?? throw new ArgumentNullException(nameof(runRepository));
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _inboxRepository = inboxRepository ?? throw new ArgumentNullException(nameof(inboxRepository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _settingsRepository = settingsRepository;
        _notificationService = notificationService;
        _memoryConsolidationService = memoryConsolidationService;
        _oneShotTimerService = oneShotTimerService;
        _correlationContextAccessor = correlationContextAccessor;
        _diagnosticsService = diagnosticsService;
        _logger = logger;
        _hostApplicationLifetime = hostApplicationLifetime;
    }

    public Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        return TickCoreAsync(ignoreEnabledFlag: true, cancellationToken);
    }

    public Task<int> TickAsync(CancellationToken cancellationToken = default)
    {
        return TickCoreAsync(ignoreEnabledFlag: false, cancellationToken);
    }

    public async Task<string?> TriggerDefinitionAsync(string definitionId, CancellationToken cancellationToken = default)
    {
        var definition = await _definitionRepository.GetByIdAsync(definitionId, cancellationToken);
        if (definition is null)
        {
            return null;
        }

        var runId = $"run-{Guid.NewGuid():N}";
        var now = _clock.UtcNow;
        var attempt = await ComputeNextAttemptAsync(definitionId, cancellationToken);
        var queuedRun = new AutomationRunRecord(
            RunId: runId,
            AutomationId: definitionId,
            Status: AutomationRunStatus.Queued,
            Trigger: ManualTrigger,
            Attempt: attempt,
            SessionId: null,
            StartedAt: now,
            CompletedAt: null,
            Summary: null,
            ErrorMessage: null);
        await _runRepository.AddAsync(queuedRun, cancellationToken);

        // Claim the scheduling slot before firing the background task.
        // This prevents the scheduler from re-triggering the same automation
        // while the manually-triggered run is still in progress.
        await ClaimDefinitionSlotAsync(definition, now, cancellationToken);

        var backgroundToken = _hostApplicationLifetime?.ApplicationStopping ?? CancellationToken.None;
        _ = Task.Run(() => RunSessionCoreAsync(definition, queuedRun, backgroundToken), CancellationToken.None);
        return runId;
    }

    private async Task<int> TickCoreAsync(bool ignoreEnabledFlag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ignoreEnabledFlag && !_options.Enabled)
        {
            return 0;
        }

        // Dynamic settings gate: check AutomationsEnabled from persisted settings.
        // This allows runtime enable/disable without restarting the Gateway.
        if (!ignoreEnabledFlag && _settingsRepository is not null)
        {
            try
            {
                var settings = await _settingsRepository.GetAsync(cancellationToken);
                if (!settings.AutomationsEnabled)
                {
                    return 0;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to read AutomationsEnabled from settings; skipping tick.");
                RecordDiagnosticEvent("automation.settings_read_failed", "warning",
                    $"Failed to read AutomationsEnabled from settings; skipping tick: {ex.GetBaseException().Message}");
                return 0;
            }
        }

        var now = _clock.UtcNow;
        await RecoverStaleRunsAsync(now, cancellationToken);

        var definitions = await _definitionRepository.ListAsync(
            new AutomationDefinitionQuery(Enabled: true, Source: null, Limit: int.MaxValue),
            cancellationToken);

        var executed = 0;
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsDue(definition, now))
            {
                continue;
            }

            await ExecuteDefinitionAsync(definition, now, cancellationToken);
            executed++;
        }

        if (_oneShotTimerService is not null)
        {
            try
            {
                await _oneShotTimerService.TickAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "One-shot timer tick failed; skipping.");
            }
        }

        return executed;
    }

    private async Task RecoverStaleRunsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var staleQueued = await _runRepository.ListAsync(
            new AutomationRunQuery(AutomationId: null, Status: AutomationRunStatus.Queued, Limit: int.MaxValue),
            cancellationToken);
        var staleRunning = await _runRepository.ListAsync(
            new AutomationRunQuery(AutomationId: null, Status: AutomationRunStatus.Running, Limit: int.MaxValue),
            cancellationToken);

        var staleThreshold = now.Subtract(_options.StaleRunThreshold);
        var staleRuns = staleQueued.Concat(staleRunning)
            .Where(r => r.StartedAt <= staleThreshold)
            .ToArray();
        foreach (var run in staleRuns)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var failedRun = run with
            {
                Status = AutomationRunStatus.Failed,
                CompletedAt = now,
                Summary = run.Summary ?? "Scheduler recovered stale run.",
                ErrorMessage = "Run was stale in queued/running state and was marked failed by scheduler recovery.",
            };
            var updated = await _runRepository.UpdateAsync(failedRun, cancellationToken);
            if (!updated)
            {
                continue;
            }

            RecordDiagnosticEvent("automation.stale_run_recovered", "warning",
                $"Stale run recovered and marked failed: automationId={failedRun.AutomationId} runId={failedRun.RunId} startedAt={run.StartedAt:O}");

            await UpsertResultInboxItemAsync(failedRun, definition: null, pushResults: null, correlationId: null, cancellationToken);
            await MarkDefinitionFailureForRecoveryAsync(failedRun.AutomationId, now, failedRun.ErrorMessage!, cancellationToken);
        }
    }

    private async Task ExecuteDefinitionAsync(
        AutomationDefinition definition,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var runId = $"run-{Guid.NewGuid():N}";
        var attempt = await ComputeNextAttemptAsync(definition.Id, cancellationToken);
        var queuedRun = new AutomationRunRecord(
            RunId: runId,
            AutomationId: definition.Id,
            Status: AutomationRunStatus.Queued,
            Trigger: SchedulerTrigger,
            Attempt: attempt,
            SessionId: null,
            StartedAt: now,
            CompletedAt: null,
            Summary: null,
            ErrorMessage: null);
        await _runRepository.AddAsync(queuedRun, cancellationToken);

        // Claim the scheduling slot before running, so that a concurrent manual
        // trigger cannot also see IsDue=true for the same definition.
        await ClaimDefinitionSlotAsync(definition, now, cancellationToken);

        await RunSessionCoreAsync(definition, queuedRun, cancellationToken);
    }

    // Pushes NextRunAt forward by StaleRunThreshold as an optimistic lock.
    // This prevents both the scheduler and a concurrent manual trigger from
    // starting a second session for the same definition before the first one
    // completes. PersistDefinitionSuccess/FailureAsync will overwrite this
    // with the real next time once the run finishes. If the process crashes
    // before that, RecoverStaleRunsAsync will set NextRunAt = now + FailureRetryDelay.
    private async Task ClaimDefinitionSlotAsync(
        AutomationDefinition definition,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var claimed = definition with { NextRunAt = now.Add(_options.StaleRunThreshold) };
        await _definitionRepository.UpsertAsync(claimed, cancellationToken);
    }

    private async Task RunSessionCoreAsync(
        AutomationDefinition definition,
        AutomationRunRecord initialRun,
        CancellationToken cancellationToken)
    {
        var runCorrelationId = Guid.NewGuid().ToString("N");
        if (_correlationContextAccessor is not null)
        {
            _correlationContextAccessor.CorrelationId = runCorrelationId;
        }

        AutomationSessionHandle? handle = null;
        var agentDisposed = false;
        var runState = initialRun;
        try
        {
            handle = await _sessionService.StartAutomationSessionAsync(definition, cancellationToken);

            runState = runState with
            {
                Status = AutomationRunStatus.Running,
                SessionId = handle.SessionId,
            };
            await _runRepository.UpdateAsync(runState, cancellationToken);

            AgentRunResult runResult;
            try
            {
                using var automationActivity = KodeAgentActivitySource.Source.StartActivity("automation.run");
                automationActivity?.SetTag("automation.id", definition.Id);
                automationActivity?.SetTag("automation.run_id", runState.RunId);
                automationActivity?.SetTag("automation.attempt", runState.Attempt);

                runResult = await handle.Agent.RunAsync("Run the scheduled automation now.", cancellationToken);
            }
            finally
            {
                await handle.Agent.DisposeAsync();
                agentDisposed = true;
            }

            if (runResult.Success)
            {
                var successfulRun = runState with
                {
                    Status = AutomationRunStatus.Succeeded,
                    CompletedAt = _clock.UtcNow,
                    Summary = NormalizeText(runResult.Response) ?? "Automation run completed successfully.",
                    ErrorMessage = null,
                };

                await _runRepository.UpdateAsync(successfulRun, cancellationToken);
                await PersistDefinitionSuccessAsync(definition, successfulRun.CompletedAt!.Value, cancellationToken);
                var pushResults = await PushToChannelsIfAutoAsync(definition, successfulRun.Summary!, cancellationToken);
                await RunPostConsolidationIfApplicableAsync(definition, cancellationToken);
                await UpsertResultInboxItemAsync(successfulRun, definition, pushResults, runCorrelationId, cancellationToken);
                RecordDiagnosticEvent("automation.run_succeeded", "info",
                    $"Automation run succeeded: automationId={definition.Id} runId={successfulRun.RunId} attempt={successfulRun.Attempt}",
                    correlationId: runCorrelationId);
                return;
            }

            var failedRun = runState with
            {
                Status = AutomationRunStatus.Failed,
                CompletedAt = _clock.UtcNow,
                Summary = NormalizeText(runResult.Response) ?? $"Automation run stopped: {runResult.StopReason}.",
                ErrorMessage = $"Run did not complete successfully (stop reason: {runResult.StopReason}).",
            };

            await _runRepository.UpdateAsync(failedRun, cancellationToken);
            await PersistDefinitionFailureAsync(definition, failedRun.CompletedAt!.Value, failedRun.ErrorMessage!, cancellationToken);
            await UpsertResultInboxItemAsync(failedRun, definition, pushResults: null, runCorrelationId, cancellationToken);
            RecordDiagnosticEvent("automation.run_failed", "warning",
                $"Automation run did not complete: automationId={definition.Id} runId={failedRun.RunId} stopReason={runResult.StopReason}",
                correlationId: runCorrelationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Automation run failed for definition {AutomationId}.", definition.Id);

            var failedAt = _clock.UtcNow;
            var failedRun = runState with
            {
                Status = AutomationRunStatus.Failed,
                CompletedAt = failedAt,
                Summary = NormalizeText(ex.GetBaseException().Message) ?? "Automation run crashed.",
                ErrorMessage = NormalizeText(ex.GetBaseException().Message) ?? "Automation run crashed.",
            };

            await _runRepository.UpdateAsync(failedRun, cancellationToken);
            await PersistDefinitionFailureAsync(definition, failedAt, failedRun.ErrorMessage!, cancellationToken);
            await UpsertResultInboxItemAsync(failedRun, definition, pushResults: null, runCorrelationId, cancellationToken);
            RecordDiagnosticEvent("automation.run_crashed", "error",
                $"Automation run crashed: automationId={definition.Id} runId={failedRun.RunId} error={ex.GetBaseException().Message}",
                correlationId: runCorrelationId);

            if (!agentDisposed && handle is { } danglingHandle)
            {
                await danglingHandle.Agent.DisposeAsync();
            }
        }
        finally
        {
            if (_correlationContextAccessor is not null)
            {
                _correlationContextAccessor.CorrelationId = null;
            }
        }
    }

    private async Task<int> ComputeNextAttemptAsync(string automationId, CancellationToken cancellationToken)
    {
        var recentRuns = await _runRepository.ListAsync(
            new AutomationRunQuery(AutomationId: automationId, Status: null, Limit: 1),
            cancellationToken);
        if (recentRuns.Count == 0)
        {
            return 1;
        }

        return recentRuns[0].Attempt + 1;
    }

    private async Task PersistDefinitionSuccessAsync(
        AutomationDefinition definition,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var updated = definition with
        {
            UpdatedAt = completedAt,
            LastRunAt = completedAt,
            NextRunAt = ComputeNextRunAt(definition.CronExpression, completedAt),
            LastRunStatus = AutomationRunStatus.Succeeded,
            LastError = null,
        };
        await _definitionRepository.UpsertAsync(updated, cancellationToken);
    }

    private async Task PersistDefinitionFailureAsync(
        AutomationDefinition definition,
        DateTimeOffset failedAt,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var updated = definition with
        {
            UpdatedAt = failedAt,
            LastRunStatus = AutomationRunStatus.Failed,
            LastError = NormalizeText(errorMessage),
            NextRunAt = failedAt.Add(_options.FailureRetryDelay),
        };
        await _definitionRepository.UpsertAsync(updated, cancellationToken);
    }

    private async Task MarkDefinitionFailureForRecoveryAsync(
        string automationId,
        DateTimeOffset now,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var definition = await _definitionRepository.GetByIdAsync(automationId, cancellationToken);
        if (definition is null || !definition.Enabled)
        {
            return;
        }

        var updated = definition with
        {
            UpdatedAt = now,
            LastRunStatus = AutomationRunStatus.Failed,
            LastError = NormalizeText(errorMessage),
            NextRunAt = now.Add(_options.FailureRetryDelay),
        };
        await _definitionRepository.UpsertAsync(updated, cancellationToken);
    }

    private async Task<IReadOnlyList<ChannelPushResult>?> PushToChannelsIfAutoAsync(
        AutomationDefinition definition,
        string text,
        CancellationToken cancellationToken)
    {
        if (definition.NotifyMode != AutomationNotifyMode.Auto
            || definition.NotificationChannels is not { Count: > 0 }
            || _notificationService is null)
        {
            return null;
        }

        try
        {
            return await _notificationService.PushAsync(definition.NotificationChannels, text, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Channel push failed for automation {AutomationId}.", definition.Id);
            return null;
        }
    }

    private async Task UpsertResultInboxItemAsync(
        AutomationRunRecord run,
        AutomationDefinition? definition,
        IReadOnlyList<ChannelPushResult>? pushResults,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        // RunId is globally unique (GUID-based), so inboxId is always new — no need to query existing.
        var inboxId = $"automation-result-{run.RunId}";
        var summary = NormalizeText(run.Summary) ?? (run.Status == AutomationRunStatus.Succeeded
            ? "Automation run completed."
            : "Automation run failed.");
        var errorMessage = NormalizeText(run.ErrorMessage);
        var payload = JsonSerializer.Serialize(new
        {
            automationId = run.AutomationId,
            runId = run.RunId,
            status = run.Status.ToString(),
            summary,
            errorMessage,
            notificationChannels = definition?.NotificationChannels,
            notifyMode = definition?.NotifyMode.ToString(),
            channelPushResults = pushResults?.Select(r => new
            {
                bindingId = r.BindingId,
                ok = r.Ok,
                errorMessage = r.ErrorMessage,
                sentAt = r.SentAt,
            }).ToArray(),
        });

        var item = new InboxItem(
            Id: inboxId,
            Kind: InboxItemKind.AutomationResult,
            Status: InboxItemStatus.Open,
            Title: $"Automation run {(run.Status == AutomationRunStatus.Succeeded ? "succeeded" : "failed")}",
            Summary: summary,
            Source: SchedulerSource,
            CreatedAt: now,
            UpdatedAt: now,
            RequiresAction: run.Status != AutomationRunStatus.Succeeded,
            Route: $"/automations/{run.AutomationId}",
            SessionId: run.SessionId,
            CorrelationId: correlationId,
            ApprovalId: null,
            PayloadJson: payload,
            ResolvedAt: null);

        await _inboxRepository.UpsertAsync(item, cancellationToken);
    }

    private async Task RunPostConsolidationIfApplicableAsync(
        AutomationDefinition definition,
        CancellationToken cancellationToken)
    {
        if (_memoryConsolidationService is null || !IsMemoryConsolidation(definition))
        {
            return;
        }

        try
        {
            await _memoryConsolidationService.PostConsolidationAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Post-consolidation hook failed for automation {AutomationId}; run status is not affected.",
                definition.Id);
        }
    }

    internal static bool IsMemoryConsolidation(AutomationDefinition definition)
    {
        var title = definition.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        return title.Contains("Memory Consolidation", StringComparison.OrdinalIgnoreCase)
               || title.Contains("记忆整合", StringComparison.Ordinal);
    }

    private static bool IsDue(AutomationDefinition definition, DateTimeOffset now)
    {
        return definition.NextRunAt is null || definition.NextRunAt <= now;
    }

    private static DateTimeOffset ComputeNextRunAt(string cronExpression, DateTimeOffset after)
        => AutomationCronComputer.ComputeNextRunAt(cronExpression, after);

    private static string? NormalizeText(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private void RecordDiagnosticEvent(string eventType, string level, string message, string? correlationId = null)
    {
        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: SchedulerSource,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: correlationId));
    }
}
