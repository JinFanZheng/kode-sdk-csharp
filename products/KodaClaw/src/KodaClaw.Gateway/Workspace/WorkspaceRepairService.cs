using System.Text.Json;
using KodaClaw.Automation;
using KodaClaw.Automation.Scheduler;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.Repair;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Store.Json;

namespace KodaClaw.Gateway;

internal sealed class WorkspaceRepairService
{
    private const string RepairScope = "startupRepair";
    private const string RepairSource = "workspace.repair";
    private const string RepairInboxId = "startup-repair-latest";
    private const string ApprovalDecisionBy = "startup.repair";
    private const int RepositorySafetyLimit = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly IWorkspaceService _workspaceService;
    private readonly IApprovalRepository _approvalRepository;
    private readonly IInboxRepository _inboxRepository;
    private readonly IAutomationRunRepository _automationRunRepository;
    private readonly IAutomationDefinitionRepository _automationDefinitionRepository;
    private readonly IPluginRegistryRepository _pluginRegistryRepository;
    private readonly IDiagnosticsService _diagnosticsService;
    private readonly AutomationSchedulerOptions _automationOptions;

    public WorkspaceRepairService(
        IWorkspaceService workspaceService,
        IApprovalRepository approvalRepository,
        IInboxRepository inboxRepository,
        IAutomationRunRepository automationRunRepository,
        IAutomationDefinitionRepository automationDefinitionRepository,
        IPluginRegistryRepository pluginRegistryRepository,
        IDiagnosticsService diagnosticsService,
        AutomationSchedulerOptions automationOptions)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _approvalRepository = approvalRepository ?? throw new ArgumentNullException(nameof(approvalRepository));
        _inboxRepository = inboxRepository ?? throw new ArgumentNullException(nameof(inboxRepository));
        _automationRunRepository = automationRunRepository ?? throw new ArgumentNullException(nameof(automationRunRepository));
        _automationDefinitionRepository = automationDefinitionRepository ?? throw new ArgumentNullException(nameof(automationDefinitionRepository));
        _pluginRegistryRepository = pluginRegistryRepository ?? throw new ArgumentNullException(nameof(pluginRegistryRepository));
        _diagnosticsService = diagnosticsService ?? throw new ArgumentNullException(nameof(diagnosticsService));
        _automationOptions = automationOptions ?? throw new ArgumentNullException(nameof(automationOptions));
    }

    public async Task<StartupRepairReportResponse> ExecuteStartupInspectionAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            try
            {
                return await ExecuteStartupInspectionCoreAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
            {
                try
                {
                    return await PersistFailureReportAsync(ex, cancellationToken);
                }
                catch (Exception persistEx) when (persistEx is not OperationCanceledException and not TaskCanceledException)
                {
                    // Failure reporting itself failed (e.g. file still locked). Return a minimal
                    // in-memory report so StartAsync does not throw and crash the host.
                    RecordDiagnosticEvent(
                        eventType: "startup_report_persist_failed",
                        level: "error",
                        message: $"PersistFailureReport threw after inspection failed. original={ex.GetBaseException().Message} persist={persistEx.GetBaseException().Message}");

                    return CreateResponse(
                        string.Empty,
                        string.Empty,
                        BuildChecklist([
                            CreateRepairItem(
                                id: "startup-repair-unrecoverable",
                                severity: RepairChecklistSeverity.Blocking,
                                state: RepairChecklistState.Pending,
                                category: "repair",
                                title: "Startup repair failed and could not be persisted",
                                summary: ex.GetBaseException().Message,
                                action: "Review diagnostics and restart the application.",
                                evidence: ex.GetType().Name),
                        ]));
                }
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<StartupRepairReportResponse> GetLatestReportAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await _workspaceService.EnsureInitializedAsync(cancellationToken);
            var reportPath = GetReportPath(snapshot.RootPath);
            if (File.Exists(reportPath))
            {
                try
                {
                    await using var stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
                    var checklist = await JsonSerializer.DeserializeAsync<RepairChecklist>(stream, JsonOptions, cancellationToken);
                    if (checklist is not null)
                    {
                        return CreateResponse(snapshot.RootPath, reportPath, checklist);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    RecordDiagnosticEvent(
                        eventType: "startup_report_invalid",
                        level: "warning",
                        message: ex.GetBaseException().Message,
                        attributes: new Dictionary<string, string?>
                        {
                            ["reportPath"] = reportPath,
                        });
                }
            }

            try
            {
                return await ExecuteStartupInspectionCoreAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
            {
                try
                {
                    return await PersistFailureReportAsync(ex, cancellationToken);
                }
                catch (Exception persistEx) when (persistEx is not OperationCanceledException and not TaskCanceledException)
                {
                    RecordDiagnosticEvent(
                        eventType: "startup_report_persist_failed",
                        level: "error",
                        message: $"PersistFailureReport threw during GetLatestReport. original={ex.GetBaseException().Message} persist={persistEx.GetBaseException().Message}");

                    return CreateResponse(
                        string.Empty,
                        string.Empty,
                        BuildChecklist([
                            CreateRepairItem(
                                id: "startup-repair-unrecoverable",
                                severity: RepairChecklistSeverity.Blocking,
                                state: RepairChecklistState.Pending,
                                category: "repair",
                                title: "Startup repair failed and could not be persisted",
                                summary: ex.GetBaseException().Message,
                                action: "Review diagnostics and restart the application.",
                                evidence: ex.GetType().Name),
                        ]));
                }
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<StartupRepairReportResponse> ExecuteStartupInspectionCoreAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _workspaceService.EnsureInitializedAsync(cancellationToken);
        var appConfig = await _workspaceService.LoadAppConfigAsync(cancellationToken);
        var items = new List<RepairChecklistItem>();

        var approvalRecovery = await RecoverPendingApprovalsAsync(cancellationToken);
        items.AddRange(approvalRecovery.Items);

        var automationItems = await RecoverAutomationRunsAsync(cancellationToken);
        items.AddRange(automationItems);

        var pluginItems = await RecoverPluginRuntimesAsync(cancellationToken);
        items.AddRange(pluginItems);

        var sessionInspection = await InspectSessionsAsync(snapshot.RootPath, appConfig, approvalRecovery, cancellationToken);
        items.AddRange(sessionInspection.Items);

        if (sessionInspection.UpdatedAppConfig != appConfig)
        {
            await _workspaceService.SaveAppConfigAsync(sessionInspection.UpdatedAppConfig, cancellationToken);
        }

        if (items.Count == 0)
        {
            items.Add(CreateRepairItem(
                id: "startup-repair-clean",
                severity: RepairChecklistSeverity.Info,
                state: RepairChecklistState.Completed,
                category: "repair",
                title: "Startup inspection found no crash residue",
                summary: "No stale approvals, automation runs, plugin runtimes, or interrupted sessions required repair.",
                action: "No operator follow-up is required."));
        }

        var checklist = BuildChecklist(items);
        var reportPath = GetReportPath(snapshot.RootPath);
        await WriteReportAsync(reportPath, checklist, cancellationToken);
        await UpsertRepairInboxItemAsync(checklist, reportPath, cancellationToken);

        RecordDiagnosticEvent(
            eventType: "startup_report_written",
            level: checklist.Summary.ActionRequiredCount > 0 || checklist.Summary.BlockingCount > 0 ? "warning" : "info",
            message: "Startup repair inspection completed.",
            attributes: new Dictionary<string, string?>
            {
                ["reportPath"] = reportPath,
                ["totalCount"] = checklist.Summary.TotalCount.ToString(),
                ["blockingCount"] = checklist.Summary.BlockingCount.ToString(),
                ["actionRequiredCount"] = checklist.Summary.ActionRequiredCount.ToString(),
                ["warningCount"] = checklist.Summary.WarningCount.ToString(),
            });

        return CreateResponse(snapshot.RootPath, reportPath, checklist);
    }

    private async Task<StartupRepairReportResponse> PersistFailureReportAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        var snapshot = await _workspaceService.EnsureInitializedAsync(cancellationToken);
        var reportPath = GetReportPath(snapshot.RootPath);
        var message = exception.GetBaseException().Message;

        var checklist = BuildChecklist(
        [
            CreateRepairItem(
                id: "startup-repair-failed",
                severity: RepairChecklistSeverity.Blocking,
                state: RepairChecklistState.Pending,
                category: "repair",
                title: "Startup repair failed before the workspace could be stabilized",
                summary: message,
                action: "Review diagnostics and rerun repair before trusting the workspace state.",
                evidence: exception.GetType().Name)
        ]);

        await WriteReportAsync(reportPath, checklist, cancellationToken);
        await UpsertRepairInboxItemAsync(checklist, reportPath, cancellationToken);
        RecordDiagnosticEvent(
            eventType: "startup_report_failed",
            level: "error",
            message: message,
            attributes: new Dictionary<string, string?>
            {
                ["reportPath"] = reportPath,
                ["exceptionType"] = exception.GetType().FullName,
            });

        return CreateResponse(snapshot.RootPath, reportPath, checklist);
    }

    private async Task<PendingApprovalRecoveryResult> RecoverPendingApprovalsAsync(CancellationToken cancellationToken)
    {
        var approvals = await _approvalRepository.ListAsync(
            new ApprovalQuery(
                Status: ApprovalStatus.Pending,
                SessionId: null,
                Kind: null,
                Limit: RepositorySafetyLimit),
            cancellationToken);

        var items = new List<RepairChecklistItem>();
        var sessionApprovalCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var approval in approvals)
        {
            var now = DateTimeOffset.UtcNow;
            var reason = approval.Kind == ApprovalKind.ChannelDelivery
                ? "Canceled stale channel delivery approval after startup repair."
                : "Canceled stale approval after startup repair.";
            var transitioned = await _approvalRepository.TransitionAsync(
                approval.Id,
                ApprovalStatus.Canceled,
                now,
                decidedBy: ApprovalDecisionBy,
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

            if (!string.IsNullOrWhiteSpace(approval.SessionId))
            {
                sessionApprovalCounts[approval.SessionId] = sessionApprovalCounts.GetValueOrDefault(approval.SessionId, 0) + 1;
            }

            var severity = approval.Kind == ApprovalKind.ChannelDelivery
                ? RepairChecklistSeverity.ActionRequired
                : RepairChecklistSeverity.Warning;
            var category = approval.Kind == ApprovalKind.ChannelDelivery ? "channels" : "approvals";
            var title = approval.Kind == ApprovalKind.ChannelDelivery
                ? "Canceled stale channel delivery approval"
                : "Canceled stale runtime approval";
            var action = approval.Kind == ApprovalKind.ChannelDelivery
                ? "Review the channel thread and resend the draft manually if it is still needed."
                : "Re-run the interrupted action from a fresh session if it is still needed.";

            items.Add(CreateRepairItem(
                id: $"approval-canceled:{approval.Id}",
                severity: severity,
                state: RepairChecklistState.Completed,
                category: category,
                title: title,
                summary: $"Approval '{approval.Title}' was still pending when the previous process exited and has been marked canceled.",
                resource: approval.Id,
                action: action,
                evidence: BuildEvidence(
                    ("kind", approval.Kind.ToString()),
                    ("sessionId", approval.SessionId),
                    ("inboxItemId", approval.InboxItemId))));

            RecordDiagnosticEvent(
                eventType: approval.Kind == ApprovalKind.ChannelDelivery
                    ? "channel_delivery_residue_canceled"
                    : "approval_residue_canceled",
                level: severity == RepairChecklistSeverity.ActionRequired ? "warning" : "info",
                message: reason,
                sessionId: approval.SessionId,
                correlationId: approval.CorrelationId,
                attributes: new Dictionary<string, string?>
                {
                    ["approvalId"] = approval.Id,
                    ["approvalKind"] = approval.Kind.ToString(),
                    ["inboxItemId"] = approval.InboxItemId,
                });
        }

        return new PendingApprovalRecoveryResult(items, sessionApprovalCounts);
    }

    private async Task<IReadOnlyList<RepairChecklistItem>> RecoverAutomationRunsAsync(CancellationToken cancellationToken)
    {
        var staleQueued = await _automationRunRepository.ListAsync(
            new AutomationRunQuery(
                AutomationId: null,
                Status: AutomationRunStatus.Queued,
                Limit: RepositorySafetyLimit),
            cancellationToken);
        var staleRunning = await _automationRunRepository.ListAsync(
            new AutomationRunQuery(
                AutomationId: null,
                Status: AutomationRunStatus.Running,
                Limit: RepositorySafetyLimit),
            cancellationToken);

        var staleRuns = staleQueued
            .Concat(staleRunning)
            .GroupBy(static run => run.RunId, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();

        var items = new List<RepairChecklistItem>(staleRuns.Length);
        foreach (var run in staleRuns)
        {
            var recoveryObservedAt = DateTimeOffset.UtcNow;
            var failedAt = recoveryObservedAt < run.StartedAt ? run.StartedAt : recoveryObservedAt;
            var repairedRun = run with
            {
                Status = AutomationRunStatus.Failed,
                CompletedAt = failedAt,
                Summary = run.Summary ?? "Startup repair recovered stale automation run.",
                ErrorMessage = "Run was still queued/running when the previous process exited and was marked failed by startup repair.",
            };

            var updated = await _automationRunRepository.UpdateAsync(repairedRun, cancellationToken);
            if (!updated)
            {
                continue;
            }

            await UpsertAutomationResultInboxItemAsync(repairedRun, cancellationToken);
            await MarkAutomationDefinitionFailureAsync(run.AutomationId, failedAt, repairedRun.ErrorMessage!, cancellationToken);

            items.Add(CreateRepairItem(
                id: $"automation-run-failed:{run.RunId}",
                severity: RepairChecklistSeverity.Warning,
                state: RepairChecklistState.Completed,
                category: "automations",
                title: "Marked stale automation run as failed",
                summary: $"Automation run '{run.RunId}' for '{run.AutomationId}' was still {run.Status} after the previous shutdown.",
                resource: run.AutomationId,
                action: "Review the automation result inbox item if the failed run needs manual follow-up.",
                evidence: BuildEvidence(
                    ("runId", run.RunId),
                    ("sessionId", run.SessionId),
                    ("nextRetryAtUtc", failedAt.Add(_automationOptions.FailureRetryDelay).ToString("O")))));

            RecordDiagnosticEvent(
                eventType: "automation_run_recovered",
                level: "warning",
                message: $"Startup repair marked automation run '{run.RunId}' as failed.",
                sessionId: run.SessionId,
                attributes: new Dictionary<string, string?>
                {
                    ["automationId"] = run.AutomationId,
                    ["runId"] = run.RunId,
                    ["previousStatus"] = run.Status.ToString(),
                });
        }

        return items;
    }

    private async Task<IReadOnlyList<RepairChecklistItem>> RecoverPluginRuntimesAsync(CancellationToken cancellationToken)
    {
        var starting = await _pluginRegistryRepository.ListAsync(
            new PluginQuery(
                Type: null,
                TrustState: null,
                Enabled: null,
                RuntimeState: PluginRuntimeState.Starting,
                Limit: RepositorySafetyLimit),
            cancellationToken);
        var running = await _pluginRegistryRepository.ListAsync(
            new PluginQuery(
                Type: null,
                TrustState: null,
                Enabled: null,
                RuntimeState: PluginRuntimeState.Running,
                Limit: RepositorySafetyLimit),
            cancellationToken);

        var stalePlugins = starting
            .Concat(running)
            .GroupBy(static plugin => plugin.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();

        var items = new List<RepairChecklistItem>(stalePlugins.Length);
        foreach (var plugin in stalePlugins)
        {
            var now = DateTimeOffset.UtcNow;
            var rootExists = Directory.Exists(plugin.RootPath);
            var runtimeState = rootExists ? PluginRuntimeState.Stopped : PluginRuntimeState.Degraded;
            var lastError = rootExists
                ? "Startup repair marked the stale plugin runtime as stopped after the previous process exited."
                : "Startup repair found a stale plugin runtime record and the plugin root no longer exists.";

            await _pluginRegistryRepository.UpsertAsync(
                plugin with
                {
                    RuntimeState = runtimeState,
                    UpdatedAt = now,
                    LastStoppedAt = now,
                    LastError = lastError,
                },
                cancellationToken);

            var severity = rootExists ? RepairChecklistSeverity.Warning : RepairChecklistSeverity.ActionRequired;
            var title = rootExists
                ? "Stopped stale plugin runtime record"
                : "Plugin runtime record needs operator repair";
            var action = rootExists
                ? "Start the plugin again if you still want it available for new sessions."
                : "Reinstall or remap the plugin root before starting it again.";

            items.Add(CreateRepairItem(
                id: $"plugin-runtime-recovered:{plugin.Id}",
                severity: severity,
                state: RepairChecklistState.Completed,
                category: "plugins",
                title: title,
                summary: $"Plugin '{plugin.Id}' was recorded as {plugin.RuntimeState} when the previous process exited and is now {runtimeState}.",
                resource: plugin.Id,
                action: action,
                evidence: BuildEvidence(
                    ("rootPath", plugin.RootPath),
                    ("previousState", plugin.RuntimeState.ToString()),
                    ("currentState", runtimeState.ToString()))));

            RecordDiagnosticEvent(
                eventType: "plugin_runtime_recovered",
                level: severity == RepairChecklistSeverity.ActionRequired ? "warning" : "info",
                message: lastError,
                attributes: new Dictionary<string, string?>
                {
                    ["pluginId"] = plugin.Id,
                    ["previousState"] = plugin.RuntimeState.ToString(),
                    ["currentState"] = runtimeState.ToString(),
                    ["rootExists"] = rootExists.ToString(),
                });
        }

        return items;
    }

    private async Task<SessionInspectionResult> InspectSessionsAsync(
        string workspaceRoot,
        WorkspaceAppConfig appConfig,
        PendingApprovalRecoveryResult approvalRecovery,
        CancellationToken cancellationToken)
    {
        var sessionsRoot = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.SessionsDirectory);
        if (!Directory.Exists(sessionsRoot))
        {
            return new SessionInspectionResult(appConfig, []);
        }

        var store = new JsonAgentStore(sessionsRoot);
        var items = new List<RepairChecklistItem>();
        var updatedAppConfig = appConfig;
        var activeMainSessionId = appConfig.ActiveMainSessionId;

        if (!string.IsNullOrWhiteSpace(activeMainSessionId))
        {
            var activeInfo = await store.LoadInfoAsync(activeMainSessionId, cancellationToken);
            if (activeInfo is null)
            {
                updatedAppConfig = updatedAppConfig with { ActiveMainSessionId = null };
                items.Add(CreateRepairItem(
                    id: $"active-main-session-missing:{activeMainSessionId}",
                    severity: RepairChecklistSeverity.Warning,
                    state: RepairChecklistState.Completed,
                    category: "sessions",
                    title: "Cleared missing active main session pointer",
                    summary: $"Workspace config referenced '{activeMainSessionId}' as the active main session, but no persisted session metadata was found.",
                    resource: activeMainSessionId,
                    action: "A fresh main session will be created on the next chat request."));

                RecordDiagnosticEvent(
                    eventType: "active_main_session_cleared",
                    level: "warning",
                    message: "Startup repair cleared a missing active main session pointer.",
                    sessionId: activeMainSessionId);
                activeMainSessionId = null;
            }
        }

        var sessionIds = Directory.EnumerateDirectories(sessionsRoot)
            .Select(static path => Path.GetFileName(path) ?? string.Empty)
            .Where(static sessionId => !string.IsNullOrWhiteSpace(sessionId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static sessionId => sessionId, StringComparer.Ordinal)
            .ToArray();

        foreach (var sessionId in sessionIds)
        {
            try
            {
                var info = await store.LoadInfoAsync(sessionId, cancellationToken);
                if (info is null)
                {
                    continue;
                }

                var toolCalls = await store.LoadToolCallRecordsAsync(sessionId, cancellationToken);
                var pendingApprovalCallCount = toolCalls.Count(static call =>
                    call.State == ToolCallState.ApprovalRequired &&
                    call.Approval.Required &&
                    string.IsNullOrWhiteSpace(call.Approval.Decision));
                var canceledApprovalCount = approvalRecovery.CanceledApprovalsBySession.GetValueOrDefault(sessionId, 0);
                var breakpointState = info.Breakpoint ?? BreakpointState.Ready;
                var isInterrupted = breakpointState != BreakpointState.Ready || pendingApprovalCallCount > 0 || canceledApprovalCount > 0;

                if (!isInterrupted)
                {
                    continue;
                }

                if (string.Equals(activeMainSessionId, sessionId, StringComparison.Ordinal) &&
                    (breakpointState == BreakpointState.AwaitingApproval || pendingApprovalCallCount > 0))
                {
                    updatedAppConfig = updatedAppConfig with { ActiveMainSessionId = null };
                    activeMainSessionId = null;

                    items.Add(CreateRepairItem(
                        id: $"active-main-session-reset:{sessionId}",
                        severity: RepairChecklistSeverity.ActionRequired,
                        state: RepairChecklistState.Completed,
                        category: "sessions",
                        title: "Cleared active main session after unsupported approval wait",
                        summary: $"Main session '{sessionId}' was waiting for approvals when the previous process exited, so startup repair cleared the active pointer instead of pretending the live wait could resume.",
                        resource: sessionId,
                        action: "Inspect the old session before replaying work, then continue in a fresh main session.",
                        evidence: BuildEvidence(
                            ("breakpoint", breakpointState.ToString()),
                            ("pendingApprovalCalls", pendingApprovalCallCount.ToString()),
                            ("canceledApprovals", canceledApprovalCount.ToString()))));

                    RecordDiagnosticEvent(
                        eventType: "active_main_session_reset",
                        level: "warning",
                        message: "Startup repair cleared the active main session after an unsupported approval wait.",
                        sessionId: sessionId,
                        attributes: new Dictionary<string, string?>
                        {
                            ["breakpoint"] = breakpointState.ToString(),
                            ["pendingApprovalCalls"] = pendingApprovalCallCount.ToString(),
                            ["canceledApprovals"] = canceledApprovalCount.ToString(),
                        });
                }

                items.Add(CreateRepairItem(
                    id: $"session-interrupted:{sessionId}",
                    severity: RepairChecklistSeverity.Warning,
                    state: RepairChecklistState.Completed,
                    category: "sessions",
                    title: "Session was left mid-flight when the process exited",
                    summary: BuildSessionSummary(sessionId, breakpointState, pendingApprovalCallCount, canceledApprovalCount),
                    resource: sessionId,
                    action: breakpointState == BreakpointState.AwaitingApproval || pendingApprovalCallCount > 0
                        ? "Review the session detail before reusing it. Approval waiters do not resume across process restarts."
                        : "Review the session detail if you need to understand where execution stopped.",
                    evidence: BuildEvidence(
                        ("sessionKind", ResolveSessionKind(sessionId).ToString()),
                        ("breakpoint", breakpointState.ToString()),
                        ("pendingApprovalCalls", pendingApprovalCallCount.ToString()),
                        ("canceledApprovals", canceledApprovalCount.ToString()))));

                RecordDiagnosticEvent(
                    eventType: "session_interrupted_detected",
                    level: "warning",
                    message: "Startup repair recorded interrupted session evidence.",
                    sessionId: sessionId,
                    attributes: new Dictionary<string, string?>
                    {
                        ["breakpoint"] = breakpointState.ToString(),
                        ["pendingApprovalCalls"] = pendingApprovalCallCount.ToString(),
                        ["canceledApprovals"] = canceledApprovalCount.ToString(),
                    });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
            {
                items.Add(CreateRepairItem(
                    id: $"session-inspection-failed:{sessionId}",
                    severity: RepairChecklistSeverity.Warning,
                    state: RepairChecklistState.Pending,
                    category: "sessions",
                    title: "Session inspection could not finish cleanly",
                    summary: $"Startup repair could not inspect session '{sessionId}': {ex.GetBaseException().Message}",
                    resource: sessionId,
                    action: "Inspect or remove the corrupted session store before trusting its state.",
                    evidence: ex.GetType().Name));

                RecordDiagnosticEvent(
                    eventType: "session_inspection_failed",
                    level: "error",
                    message: ex.GetBaseException().Message,
                    sessionId: sessionId);
            }
        }

        return new SessionInspectionResult(updatedAppConfig, items);
    }

    private async Task MarkAutomationDefinitionFailureAsync(
        string automationId,
        DateTimeOffset now,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var definition = await _automationDefinitionRepository.GetByIdAsync(automationId, cancellationToken);
        if (definition is null || !definition.Enabled)
        {
            return;
        }

        await _automationDefinitionRepository.UpsertAsync(
            definition with
            {
                UpdatedAt = now,
                LastRunStatus = AutomationRunStatus.Failed,
                LastError = NormalizeText(errorMessage),
                NextRunAt = now.Add(_automationOptions.FailureRetryDelay),
            },
            cancellationToken);
    }

    private async Task UpsertAutomationResultInboxItemAsync(
        AutomationRunRecord run,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var inboxId = $"automation-result-{run.RunId}";
        var existing = await _inboxRepository.GetByIdAsync(inboxId, cancellationToken);
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
        }, JsonOptions);

        await _inboxRepository.UpsertAsync(
            new InboxItem(
                Id: inboxId,
                Kind: InboxItemKind.AutomationResult,
                Status: InboxItemStatus.Open,
                Title: $"Automation run {(run.Status == AutomationRunStatus.Succeeded ? "succeeded" : "failed")}",
                Summary: summary,
                Source: "automation.scheduler",
                CreatedAt: existing?.CreatedAt ?? now,
                UpdatedAt: now,
                RequiresAction: run.Status != AutomationRunStatus.Succeeded,
                Route: $"/automations/{run.AutomationId}",
                SessionId: run.SessionId,
                CorrelationId: existing?.CorrelationId,
                ApprovalId: null,
                PayloadJson: payload,
                ResolvedAt: null),
            cancellationToken);
    }

    private async Task UpsertRepairInboxItemAsync(
        RepairChecklist checklist,
        string reportPath,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await _inboxRepository.GetByIdAsync(RepairInboxId, cancellationToken);
        var requiresAction = checklist.Items.Any(static item =>
            item.Severity is RepairChecklistSeverity.ActionRequired or RepairChecklistSeverity.Blocking ||
            item.State == RepairChecklistState.Pending);
        var status = requiresAction ? InboxItemStatus.Open : InboxItemStatus.Resolved;
        var payload = JsonSerializer.Serialize(new
        {
            reportPath,
            generatedAt = checklist.GeneratedAt,
            summary = checklist.Summary,
        }, JsonOptions);

        await _inboxRepository.UpsertAsync(
            new InboxItem(
                Id: RepairInboxId,
                Kind: InboxItemKind.Alert,
                Status: status,
                Title: requiresAction ? "Startup repair requires review" : "Startup repair completed",
                Summary: requiresAction
                    ? $"Startup repair found {checklist.Summary.ActionRequiredCount + checklist.Summary.BlockingCount} item(s) that need operator review."
                    : "Startup repair completed without requiring operator follow-up.",
                Source: RepairSource,
                CreatedAt: existing?.CreatedAt ?? now,
                UpdatedAt: now,
                RequiresAction: requiresAction,
                Route: "/diagnostics",
                SessionId: null,
                CorrelationId: existing?.CorrelationId,
                ApprovalId: null,
                PayloadJson: payload,
                ResolvedAt: requiresAction ? null : now),
            cancellationToken);
    }

    private static string BuildSessionSummary(
        string sessionId,
        BreakpointState breakpointState,
        int pendingApprovalCallCount,
        int canceledApprovalCount)
    {
        var parts = new List<string>
        {
            $"Session '{sessionId}' was persisted at breakpoint {breakpointState}."
        };

        if (pendingApprovalCallCount > 0)
        {
            parts.Add($"{pendingApprovalCallCount} approval-required tool call(s) remain in the persisted tool timeline.");
        }

        if (canceledApprovalCount > 0)
        {
            parts.Add($"Startup repair canceled {canceledApprovalCount} pending approval record(s) linked to this session.");
        }

        return string.Join(" ", parts);
    }

    private static string? NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static RepairChecklist BuildChecklist(IReadOnlyList<RepairChecklistItem> items)
    {
        var blockingCount = items.Count(static item => item.Severity == RepairChecklistSeverity.Blocking);
        var actionRequiredCount = items.Count(static item => item.Severity == RepairChecklistSeverity.ActionRequired);
        var warningCount = items.Count(static item => item.Severity == RepairChecklistSeverity.Warning);
        var infoCount = items.Count(static item => item.Severity == RepairChecklistSeverity.Info);

        return new RepairChecklist(
            GeneratedAt: DateTimeOffset.UtcNow,
            Scope: RepairScope,
            Summary: new RepairChecklistSummary(
                TotalCount: items.Count,
                BlockingCount: blockingCount,
                ActionRequiredCount: actionRequiredCount,
                WarningCount: warningCount,
                InfoCount: infoCount),
            Items: items
                .OrderByDescending(static item => item.Severity)
                .ThenBy(static item => item.Id, StringComparer.Ordinal)
                .ToArray());
    }

    private static RepairChecklistItem CreateRepairItem(
        string id,
        RepairChecklistSeverity severity,
        RepairChecklistState state,
        string category,
        string title,
        string summary,
        string? resource = null,
        string? action = null,
        string? evidence = null)
    {
        return new RepairChecklistItem(
            Id: id,
            Severity: severity,
            State: state,
            Category: category,
            Title: title,
            Summary: summary,
            Resource: resource,
            Action: action,
            Evidence: evidence);
    }

    private static StartupRepairReportResponse CreateResponse(
        string workspaceRoot,
        string reportPath,
        RepairChecklist checklist)
    {
        return new StartupRepairReportResponse(
            GeneratedAt: checklist.GeneratedAt,
            WorkspaceRootPath: workspaceRoot,
            ReportPath: reportPath,
            Checklist: checklist);
    }

    private void RecordDiagnosticEvent(
        string eventType,
        string level,
        string message,
        string? sessionId = null,
        string? correlationId = null,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        _diagnosticsService.Record(new DiagnosticEvent(
            Id: $"diag-startup-repair-{Guid.NewGuid():N}",
            Source: RepairSource,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: correlationId,
            SessionId: sessionId,
            Attributes: attributes));
    }

    private static string BuildEvidence(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(static pair => $"{pair.Key}={pair.Value}")
            .ToArray();

        return parts.Length == 0 ? string.Empty : string.Join("; ", parts);
    }

    private static SessionKind ResolveSessionKind(string sessionId)
    {
        if (sessionId.StartsWith("channel-", StringComparison.OrdinalIgnoreCase)
            && sessionId.Contains("-dm-", StringComparison.OrdinalIgnoreCase))
        {
            return SessionKind.ChannelDirectMessage;
        }

        if (sessionId.StartsWith("channel-", StringComparison.OrdinalIgnoreCase)
            && sessionId.Contains("-group-", StringComparison.OrdinalIgnoreCase))
        {
            return SessionKind.ChannelGroup;
        }

        if (sessionId.StartsWith("auto-", StringComparison.OrdinalIgnoreCase))
        {
            return SessionKind.Automation;
        }

        if (sessionId.StartsWith("plugin-", StringComparison.OrdinalIgnoreCase))
        {
            return SessionKind.Plugin;
        }

        return SessionKind.Main;
    }

    private static string GetReportPath(string workspaceRoot)
    {
        return Path.Combine(
            workspaceRoot,
            KodaClawWorkspaceLayout.ConfigDirectory,
            KodaClawWorkspaceLayout.StartupRepairReportFile);
    }

    private static async Task WriteReportAsync(
        string reportPath,
        RepairChecklist checklist,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await using var stream = File.Create(reportPath);
        await JsonSerializer.SerializeAsync(stream, checklist, JsonOptions, cancellationToken);
    }

    private sealed record PendingApprovalRecoveryResult(
        IReadOnlyList<RepairChecklistItem> Items,
        IReadOnlyDictionary<string, int> CanceledApprovalsBySession);

    private sealed record SessionInspectionResult(
        WorkspaceAppConfig UpdatedAppConfig,
        IReadOnlyList<RepairChecklistItem> Items);
}
