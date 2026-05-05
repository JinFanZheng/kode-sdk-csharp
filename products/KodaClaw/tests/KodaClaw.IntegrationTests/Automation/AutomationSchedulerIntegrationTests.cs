using System.Text.Json;
using FluentAssertions;
using KodaClaw.Automation;
using KodaClaw.Automation.Scheduler;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Settings;
using KodaClaw.ControlPlane;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace KodaClaw.IntegrationTests.Automation;

public sealed class AutomationSchedulerIntegrationTests
{
    [Fact]
    public async Task Due_automation_should_be_scheduled_and_persist_run_history()
    {
        var now = new DateTimeOffset(2026, 3, 19, 8, 0, 0, TimeSpan.Zero);
        using var fixture = new SchedulerFixture(now);
        fixture.SessionService.SetResult(
            "auto-due-success",
            new AgentRunResult
            {
                Success = true,
                Response = "Daily digest generated.",
                StopReason = StopReason.EndTurn,
            });

        var definition = fixture.BuildDefinition(
            id: "auto-due-success",
            cronExpression: "0 */2 * * *",
            nextRunAt: now.AddMinutes(-1));
        await fixture.Definitions.UpsertAsync(definition);

        var executed = await fixture.Scheduler.TickAsync();
        var runs = await fixture.Runs.ListAsync(new AutomationRunQuery(
            AutomationId: definition.Id,
            Status: null,
            Limit: 10));
        var reloadedDefinition = await fixture.Definitions.GetByIdAsync(definition.Id);

        executed.Should().Be(1);
        runs.Should().ContainSingle();
        runs[0].Status.Should().Be(AutomationRunStatus.Succeeded);
        runs[0].Trigger.Should().Be("automation.scheduler");
        runs[0].Summary.Should().Be("Daily digest generated.");
        reloadedDefinition.Should().NotBeNull();
        reloadedDefinition!.LastRunStatus.Should().Be(AutomationRunStatus.Succeeded);
        reloadedDefinition.LastRunAt.Should().Be(now);
        reloadedDefinition.NextRunAt.Should().Be(now.AddHours(2));
    }

    [Fact]
    public async Task Scheduler_should_upsert_inbox_item_for_success_and_failure_runs()
    {
        var now = new DateTimeOffset(2026, 3, 19, 9, 0, 0, TimeSpan.Zero);
        using var fixture = new SchedulerFixture(now);
        fixture.SessionService.SetResult(
            "auto-success",
            new AgentRunResult
            {
                Success = true,
                Response = "Success summary.",
                StopReason = StopReason.EndTurn,
            });
        fixture.SessionService.SetResult(
            "auto-failure",
            new AgentRunResult
            {
                Success = false,
                Response = "Failure summary.",
                StopReason = StopReason.Error,
            });

        await fixture.Definitions.UpsertAsync(fixture.BuildDefinition("auto-success", nextRunAt: null));
        await fixture.Definitions.UpsertAsync(fixture.BuildDefinition("auto-failure", nextRunAt: null));

        var executed = await fixture.Scheduler.TickAsync();
        var successRun = (await fixture.Runs.ListAsync(new AutomationRunQuery("auto-success", null, 10))).Single();
        var failureRun = (await fixture.Runs.ListAsync(new AutomationRunQuery("auto-failure", null, 10))).Single();

        var successInbox = await fixture.Inbox.GetByIdAsync($"automation-result-{successRun.RunId}");
        var failureInbox = await fixture.Inbox.GetByIdAsync($"automation-result-{failureRun.RunId}");
        var failureDefinition = await fixture.Definitions.GetByIdAsync("auto-failure");

        executed.Should().Be(2);

        successRun.Status.Should().Be(AutomationRunStatus.Succeeded);
        successInbox.Should().NotBeNull();
        successInbox!.Kind.Should().Be(InboxItemKind.AutomationResult);
        successInbox.Source.Should().Be("automation.scheduler");
        successInbox.Route.Should().Be("/automations/auto-success");
        successInbox.RequiresAction.Should().BeFalse();
        JsonDocument.Parse(successInbox.PayloadJson!).RootElement.GetProperty("status").GetString().Should().Be("Succeeded");

        failureRun.Status.Should().Be(AutomationRunStatus.Failed);
        failureInbox.Should().NotBeNull();
        failureInbox!.Kind.Should().Be(InboxItemKind.AutomationResult);
        failureInbox.Source.Should().Be("automation.scheduler");
        failureInbox.Route.Should().Be("/automations/auto-failure");
        failureInbox.RequiresAction.Should().BeTrue();
        JsonDocument.Parse(failureInbox.PayloadJson!).RootElement.GetProperty("status").GetString().Should().Be("Failed");

        failureDefinition.Should().NotBeNull();
        failureDefinition!.LastRunStatus.Should().Be(AutomationRunStatus.Failed);
        failureDefinition.LastError.Should().NotBeNullOrWhiteSpace();
        failureDefinition.NextRunAt.Should().Be(now.AddMinutes(15));
    }

    [Fact]
    public async Task Recovery_should_fail_stale_runs_and_allow_follow_up_reschedule()
    {
        var now = new DateTimeOffset(2026, 3, 19, 10, 0, 0, TimeSpan.Zero);
        using var fixture = new SchedulerFixture(now);
        fixture.SessionService.SetResult(
            "auto-recovery",
            new AgentRunResult
            {
                Success = true,
                Response = "Recovered and reran.",
                StopReason = StopReason.EndTurn,
            });

        var definition = fixture.BuildDefinition(
            id: "auto-recovery",
            nextRunAt: now.AddDays(1));
        await fixture.Definitions.UpsertAsync(definition);
        await fixture.Runs.AddAsync(new AutomationRunRecord(
            RunId: "run-stale-001",
            AutomationId: definition.Id,
            Status: AutomationRunStatus.Running,
            Trigger: "automation.scheduler",
            Attempt: 1,
            SessionId: "session-stale-001",
            StartedAt: now.AddHours(-3),
            CompletedAt: null,
            Summary: null,
            ErrorMessage: null));

        var firstTickExecuted = await fixture.Scheduler.TickAsync();
        var runsAfterRecovery = await fixture.Runs.ListAsync(new AutomationRunQuery(
            AutomationId: definition.Id,
            Status: null,
            Limit: 10));
        var staleRun = runsAfterRecovery.Single(run => run.RunId == "run-stale-001");
        var staleInbox = await fixture.Inbox.GetByIdAsync("automation-result-run-stale-001");
        var definitionAfterRecovery = await fixture.Definitions.GetByIdAsync(definition.Id);

        firstTickExecuted.Should().Be(0);
        staleRun.Status.Should().Be(AutomationRunStatus.Failed);
        staleRun.CompletedAt.Should().Be(now);
        staleInbox.Should().NotBeNull();
        staleInbox!.RequiresAction.Should().BeTrue();
        fixture.SessionService.GetStartCount(definition.Id).Should().Be(0);

        definitionAfterRecovery.Should().NotBeNull();
        definitionAfterRecovery!.LastRunStatus.Should().Be(AutomationRunStatus.Failed);
        definitionAfterRecovery.LastError.Should().NotBeNullOrWhiteSpace();
        definitionAfterRecovery.NextRunAt.Should().Be(now.AddMinutes(15));

        fixture.Clock.SetUtcNow(now.AddMinutes(15));
        var secondTickExecuted = await fixture.Scheduler.TickAsync();
        var runsAfterRetry = await fixture.Runs.ListAsync(new AutomationRunQuery(
            AutomationId: definition.Id,
            Status: null,
            Limit: 10));
        var rerun = runsAfterRetry.Single(run => run.RunId != "run-stale-001");
        var rerunInbox = await fixture.Inbox.GetByIdAsync($"automation-result-{rerun.RunId}");
        var definitionAfterRetry = await fixture.Definitions.GetByIdAsync(definition.Id);

        secondTickExecuted.Should().Be(1);
        rerun.Status.Should().Be(AutomationRunStatus.Succeeded);
        rerun.CompletedAt.Should().Be(now.AddMinutes(15));
        rerunInbox.Should().NotBeNull();
        rerunInbox!.RequiresAction.Should().BeFalse();
        fixture.SessionService.GetStartCount(definition.Id).Should().Be(1);

        definitionAfterRetry.Should().NotBeNull();
        definitionAfterRetry!.LastRunStatus.Should().Be(AutomationRunStatus.Succeeded);
        definitionAfterRetry.LastRunAt.Should().Be(now.AddMinutes(15));
    }

    [Fact]
    public async Task Tick_should_skip_execution_when_AutomationsEnabled_is_false_in_settings()
    {
        var now = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        using var fixture = new SchedulerFixture(now);
        fixture.SessionService.SetResult(
            "auto-settings-gate",
            new AgentRunResult
            {
                Success = true,
                Response = "Should not run.",
                StopReason = StopReason.EndTurn,
            });

        await fixture.Definitions.UpsertAsync(fixture.BuildDefinition("auto-settings-gate", nextRunAt: null));

        // Disable automations via settings
        await fixture.Settings.SaveAsync(KodaClawSettings.Default with
        {
            AutomationsEnabled = false,
            UpdatedAt = now,
        });

        var executed = await fixture.Scheduler.TickAsync();

        executed.Should().Be(0);
        fixture.SessionService.GetStartCount("auto-settings-gate").Should().Be(0);
        var runs = await fixture.Runs.ListAsync(new AutomationRunQuery(
            AutomationId: "auto-settings-gate",
            Status: null,
            Limit: 10));
        runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_should_execute_when_AutomationsEnabled_is_true_in_settings()
    {
        var now = new DateTimeOffset(2026, 3, 20, 10, 0, 0, TimeSpan.Zero);
        using var fixture = new SchedulerFixture(now);
        fixture.SessionService.SetResult(
            "auto-settings-enabled",
            new AgentRunResult
            {
                Success = true,
                Response = "Ran with gate open.",
                StopReason = StopReason.EndTurn,
            });

        await fixture.Definitions.UpsertAsync(fixture.BuildDefinition("auto-settings-enabled", nextRunAt: null));

        // Enable automations via settings
        var settings = await fixture.Settings.GetAsync();
        await fixture.Settings.SaveAsync(settings with
        {
            AutomationsEnabled = true,
            UpdatedAt = now,
        });

        var executed = await fixture.Scheduler.TickAsync();
        var runs = await fixture.Runs.ListAsync(new AutomationRunQuery(
            AutomationId: "auto-settings-enabled",
            Status: null,
            Limit: 10));

        executed.Should().Be(1);
        fixture.SessionService.GetStartCount("auto-settings-enabled").Should().Be(1);
        runs.Should().ContainSingle(r => r.Status == AutomationRunStatus.Succeeded);
    }

    [Fact]
    public async Task Fake_clock_with_manual_tick_should_drive_next_run_changes()
    {
        var now = new DateTimeOffset(2026, 3, 19, 8, 0, 0, TimeSpan.Zero);
        using var fixture = new SchedulerFixture(
            now,
            configureScheduler: options => options.Enabled = false);
        fixture.SessionService.SetResult(
            "auto-daily",
            new AgentRunResult
            {
                Success = true,
                Response = "Daily run completed.",
                StopReason = StopReason.EndTurn,
            });

        var definition = fixture.BuildDefinition(
            id: "auto-daily",
            cronExpression: "0 9 * * *",
            nextRunAt: null);
        await fixture.Definitions.UpsertAsync(definition);

        var disabledTick = await fixture.Scheduler.TickAsync();
        var firstManualRun = await fixture.Scheduler.RunOnceAsync();
        var afterFirstRun = await fixture.Definitions.GetByIdAsync(definition.Id);

        // Advance clock to the computed next-run time so the automation becomes due again.
        // This is timezone-agnostic: we drive the clock to wherever ComputeNextRunAt placed it.
        fixture.Clock.SetUtcNow(afterFirstRun!.NextRunAt!.Value);
        var secondManualRun = await fixture.Scheduler.RunOnceAsync();
        var afterSecondRun = await fixture.Definitions.GetByIdAsync(definition.Id);

        var expectedAfterFirst = AutomationCronComputer.ComputeNextRunAt("0 9 * * *", now);
        var expectedAfterSecond = AutomationCronComputer.ComputeNextRunAt("0 9 * * *", afterFirstRun.NextRunAt!.Value);

        disabledTick.Should().Be(0);
        firstManualRun.Should().Be(1);
        afterFirstRun!.NextRunAt.Should().Be(expectedAfterFirst);

        secondManualRun.Should().Be(1);
        afterSecondRun!.NextRunAt.Should().Be(expectedAfterSecond);
    }

    [Fact]
    public async Task Auto_notify_mode_should_call_notification_service_with_configured_binding_ids()
    {
        var now = new DateTimeOffset(2026, 3, 19, 8, 0, 0, TimeSpan.Zero);
        var notificationService = new CapturingNotificationService();
        using var fixture = new SchedulerFixture(now, notificationService: notificationService);
        fixture.SessionService.SetResult(
            "auto-push-test",
            new AgentRunResult { Success = true, Response = "Push summary.", StopReason = StopReason.EndTurn });

        var definition = fixture.BuildDefinition(
            id: "auto-push-test",
            notificationChannels: ["tg-main-abc", "feishu-ops-xyz"],
            notifyMode: AutomationNotifyMode.Auto);
        await fixture.Definitions.UpsertAsync(definition);

        await fixture.Scheduler.TickAsync();

        notificationService.LastBindingIds.Should().Equal("tg-main-abc", "feishu-ops-xyz");
        notificationService.LastText.Should().Be("Push summary.");
    }

    [Fact]
    public async Task Auto_notify_success_with_external_message_id_should_create_channel_message_link()
    {
        var now = new DateTimeOffset(2026, 3, 19, 8, 0, 0, TimeSpan.Zero);
        var notificationService = new CapturingNotificationService(
            successMetadata: new ChannelPushSuccessMetadata(
                ExternalMessageId: "msg-telegram-001",
                ConnectorKind: ChannelConnectorKind.Telegram,
                AccountId: "telegram-main",
                ExternalThreadId: "12345"));
        using var fixture = new SchedulerFixture(now, notificationService: notificationService);
        fixture.SessionService.SetResult(
            "auto-push-link",
            new AgentRunResult { Success = true, Response = "Push summary.", StopReason = StopReason.EndTurn });

        var definition = fixture.BuildDefinition(
            id: "auto-push-link",
            notificationChannels: ["tg-main-abc"],
            notifyMode: AutomationNotifyMode.Auto);
        await fixture.Definitions.UpsertAsync(definition);

        await fixture.Scheduler.TickAsync();

        var runs = await fixture.Runs.ListAsync(new AutomationRunQuery(AutomationId: "auto-push-link", Limit: 10));
        runs.Should().ContainSingle();
        var link = await fixture.MessageLinks.GetByExternalMessageAsync(
            ChannelConnectorKind.Telegram,
            "telegram-main",
            "12345",
            "msg-telegram-001");
        link.Should().NotBeNull();
        link!.AutomationId.Should().Be("auto-push-link");
        link.RunId.Should().Be(runs[0].RunId);
        link.SessionId.Should().Be(runs[0].SessionId);
        link.BindingId.Should().Be("tg-main-abc");
        link.ExpiresAt.Should().Be(now.AddDays(30));
    }

    [Fact]
    public async Task None_notify_mode_should_not_call_notification_service()
    {
        var now = new DateTimeOffset(2026, 3, 19, 8, 0, 0, TimeSpan.Zero);
        var notificationService = new CapturingNotificationService();
        using var fixture = new SchedulerFixture(now, notificationService: notificationService);
        fixture.SessionService.SetResult(
            "auto-no-push",
            new AgentRunResult { Success = true, Response = "Done.", StopReason = StopReason.EndTurn });

        var definition = fixture.BuildDefinition(
            id: "auto-no-push",
            notificationChannels: ["tg-main-abc"],
            notifyMode: AutomationNotifyMode.None);
        await fixture.Definitions.UpsertAsync(definition);

        await fixture.Scheduler.TickAsync();

        notificationService.LastBindingIds.Should().BeNull();
    }

    [Fact]
    public async Task Manual_trigger_claims_slot_so_scheduler_tick_does_not_double_fire()
    {
        // Scenario: user manually triggers an automation that is also due on the scheduler.
        // The manual trigger must claim the scheduling slot (push NextRunAt forward) BEFORE
        // firing its background task, so the very next tick sees IsDue=false and skips it.
        var now = new DateTimeOffset(2026, 3, 20, 10, 0, 0, TimeSpan.Zero);
        using var fixture = new SchedulerFixture(now);
        fixture.SessionService.SetResult(
            "auto-overlap",
            new AgentRunResult { Success = true, Response = "Manual run.", StopReason = StopReason.EndTurn });

        // Automation is overdue (NextRunAt = null → IsDue=true on any tick).
        var definition = fixture.BuildDefinition("auto-overlap", nextRunAt: null);
        await fixture.Definitions.UpsertAsync(definition);

        // Manually trigger. TriggerDefinitionAsync claims the slot synchronously before
        // returning, even though the background Agent task runs asynchronously.
        var runId = await fixture.Scheduler.TriggerDefinitionAsync("auto-overlap");

        // The slot must already be claimed in DB at this point (before any tick).
        var definitionAfterTrigger = await fixture.Definitions.GetByIdAsync("auto-overlap");
        definitionAfterTrigger!.NextRunAt.Should().BeAfter(now,
            "the definition slot must be claimed before the background task completes");

        // A scheduler tick arriving while the manual run is still in-flight must be a no-op.
        var tickExecuted = await fixture.Scheduler.TickAsync();

        // Allow the background fire-and-forget task to finish so the test cleanup is clean.
        await Task.Delay(200);

        runId.Should().NotBeNullOrEmpty();
        tickExecuted.Should().Be(0, "the slot was already claimed by the manual trigger");
        fixture.SessionService.GetStartCount("auto-overlap").Should().Be(1,
            "only the manual trigger should have started an agent session");
    }

    [Fact]
    public async Task Channel_push_failure_should_record_in_inbox_payload_without_affecting_run_status()
    {
        var now = new DateTimeOffset(2026, 3, 19, 8, 0, 0, TimeSpan.Zero);
        var notificationService = new CapturingNotificationService(failureMessage: "Binding not found");
        using var fixture = new SchedulerFixture(now, notificationService: notificationService);
        fixture.SessionService.SetResult(
            "auto-push-fail",
            new AgentRunResult { Success = true, Response = "Success despite push fail.", StopReason = StopReason.EndTurn });

        var definition = fixture.BuildDefinition(
            id: "auto-push-fail",
            notificationChannels: ["bad-binding-id"],
            notifyMode: AutomationNotifyMode.Auto);
        await fixture.Definitions.UpsertAsync(definition);

        await fixture.Scheduler.TickAsync();

        var runs = await fixture.Runs.ListAsync(new AutomationRunQuery(AutomationId: "auto-push-fail", Status: null, Limit: 10));
        runs.Should().ContainSingle();
        runs[0].Status.Should().Be(AutomationRunStatus.Succeeded);

        var inboxItems = await fixture.Inbox.ListAsync(new InboxQuery(Limit: 10));
        var resultItem = inboxItems.FirstOrDefault(i =>
            i.Route == "/automations/auto-push-fail" && i.Kind == InboxItemKind.AutomationResult);
        resultItem.Should().NotBeNull();

        var payload = JsonDocument.Parse(resultItem!.PayloadJson!);
        payload.RootElement.TryGetProperty("channelPushResults", out var pushResultsEl).Should().BeTrue();
        var firstResult = pushResultsEl.EnumerateArray().First();
        firstResult.GetProperty("ok").GetBoolean().Should().BeFalse();
        firstResult.GetProperty("errorMessage").GetString().Should().Contain("Binding not found");
    }

    private sealed class SchedulerFixture : IDisposable
    {
        private readonly ServiceProvider _provider;

        public SchedulerFixture(
            DateTimeOffset initialUtcNow,
            Action<AutomationSchedulerOptions>? configureScheduler = null,
            IAutomationNotificationService? notificationService = null)
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "kodaclaw-automation-scheduler-integration",
                Guid.NewGuid().ToString("N"));
            Clock = new FakeAutomationClock(initialUtcNow);
            SessionService = new StubAutomationSessionService();

            var services = new ServiceCollection();
            services.AddKodaClawWorkspace(options => options.RootPath = RootPath);
            services.AddKodaClawJsonStore(RootPath);
            services.AddKodaClawControlPlane();
            services.AddSingleton<IAutomationClock>(Clock);
            services.AddSingleton<IAutomationSessionService>(SessionService);
            if (notificationService is not null)
            {
                services.AddSingleton<IAutomationNotificationService>(notificationService);
            }

            services.AddKodaClawAutomation(options =>
            {
                options.Enabled = true;
                options.PollInterval = TimeSpan.FromMinutes(1);
                options.FailureRetryDelay = TimeSpan.FromMinutes(15);
                configureScheduler?.Invoke(options);
            });

            _provider = services.BuildServiceProvider();
            Definitions = _provider.GetRequiredService<IAutomationDefinitionRepository>();
            Runs = _provider.GetRequiredService<IAutomationRunRepository>();
            MessageLinks = _provider.GetRequiredService<IAutomationChannelMessageLinkRepository>();
            Inbox = _provider.GetRequiredService<IInboxRepository>();
            Settings = _provider.GetRequiredService<ISettingsRepository>();
            Scheduler = _provider.GetRequiredService<IAutomationScheduler>();

            // Enable automations by default so scheduler integration tests can tick freely.
            // Individual tests that want to test the disabled state override this after construction.
            Settings.SaveAsync(KodaClawSettings.Default with
            {
                AutomationsEnabled = true,
                UpdatedAt = initialUtcNow,
            }).GetAwaiter().GetResult();
        }

        public string RootPath { get; }

        public FakeAutomationClock Clock { get; }

        public StubAutomationSessionService SessionService { get; }

        public IAutomationDefinitionRepository Definitions { get; }

        public IAutomationRunRepository Runs { get; }

        public IAutomationChannelMessageLinkRepository MessageLinks { get; }

        public IInboxRepository Inbox { get; }

        public ISettingsRepository Settings { get; }

        public IAutomationScheduler Scheduler { get; }

        public AutomationDefinition BuildDefinition(
            string id,
            string? cronExpression = null,
            DateTimeOffset? nextRunAt = null,
            IReadOnlyList<string>? notificationChannels = null,
            AutomationNotifyMode notifyMode = AutomationNotifyMode.None)
        {
            return new AutomationDefinition(
                Id: id,
                Title: $"Automation {id}",
                Prompt: $"Run automation {id}.",
                Source: AutomationDefinitionSource.Manual,
                SourcePath: null,
                CronExpression: cronExpression ?? "0 * * * *",
                Enabled: true,
                InputPaths: null,
                ModelId: null,
                NotificationChannels: notificationChannels,
                NotifyMode: notifyMode,
                CreatedAt: Clock.UtcNow,
                UpdatedAt: Clock.UtcNow,
                LastRunAt: null,
                NextRunAt: nextRunAt,
                LastRunStatus: null,
                LastError: null);
        }

        public void Dispose()
        {
            _provider.Dispose();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class CapturingNotificationService : IAutomationNotificationService
    {
        private readonly string? _failureMessage;
        private readonly ChannelPushSuccessMetadata? _successMetadata;

        public CapturingNotificationService(
            string? failureMessage = null,
            ChannelPushSuccessMetadata? successMetadata = null)
        {
            _failureMessage = failureMessage;
            _successMetadata = successMetadata;
        }

        public IReadOnlyList<string>? LastBindingIds { get; private set; }

        public string? LastText { get; private set; }

        public Task<IReadOnlyList<ChannelPushResult>> PushAsync(
            IReadOnlyList<string> bindingIds,
            string text,
            CancellationToken cancellationToken = default)
        {
            LastBindingIds = bindingIds;
            LastText = text;

            var results = bindingIds.Select(id => _failureMessage is null
                ? new ChannelPushResult(
                    id,
                    Ok: true,
                    ErrorMessage: null,
                    SentAt: DateTimeOffset.UtcNow,
                    ExternalMessageId: _successMetadata?.ExternalMessageId,
                    ConnectorKind: _successMetadata?.ConnectorKind,
                    AccountId: _successMetadata?.AccountId,
                    ExternalThreadId: _successMetadata?.ExternalThreadId)
                : new ChannelPushResult(id, Ok: false, ErrorMessage: _failureMessage, SentAt: null))
                .ToArray();

            return Task.FromResult<IReadOnlyList<ChannelPushResult>>(results);
        }
    }

    private sealed record ChannelPushSuccessMetadata(
        string ExternalMessageId,
        ChannelConnectorKind ConnectorKind,
        string AccountId,
        string ExternalThreadId);

    private sealed class StubAutomationSessionService : IAutomationSessionService
    {
        private readonly Dictionary<string, AgentRunResult> _resultsByAutomationId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _startCounts = new(StringComparer.Ordinal);

        public void SetResult(string automationId, AgentRunResult result)
        {
            _resultsByAutomationId[automationId] = result;
        }

        public int GetStartCount(string automationId)
        {
            return _startCounts.TryGetValue(automationId, out var count) ? count : 0;
        }

        public Task<AutomationSessionHandle> StartAutomationSessionAsync(
            AutomationDefinition definition,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _startCounts[definition.Id] = GetStartCount(definition.Id) + 1;
            var runResult = _resultsByAutomationId.TryGetValue(definition.Id, out var configured)
                ? configured
                : new AgentRunResult
                {
                    Success = true,
                    Response = "Default success.",
                    StopReason = StopReason.EndTurn,
                };

            var agent = new Mock<IAgent>();
            agent.SetupGet(value => value.AgentId).Returns($"agent-{definition.Id}");
            agent.Setup(value => value.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(runResult);

            var handle = new AutomationSessionHandle(
                SessionId: $"session-{definition.Id}-{_startCounts[definition.Id]}",
                AutomationId: definition.Id,
                SessionKind: SessionKind.Automation,
                SessionDirectory: "/tmp",
                Agent: agent.Object);
            return Task.FromResult(handle);
        }
    }
}
