using FluentAssertions;
using KodaClaw.Automation;
using KodaClaw.Automation.Scheduler;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Jobs;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Settings;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Automation;

/// <summary>
/// Unit tests for AutomationScheduler diagnostic event emission (KC-6204 / KC-6206).
/// </summary>
public sealed class AutomationSchedulerDiagnosticsTests
{
    [Fact]
    public async Task RunSucceeded_should_emit_automation_run_succeeded_event()
    {
        var definitionRepo = new InMemoryDefinitionRepo();
        await definitionRepo.UpsertAsync(BuildDefinition("auto-ok", nextRunAt: null));

        var diagnostics = new CollectingDiagnosticsService();
        var (scheduler, sessionService) = BuildScheduler(
            definitionRepository: definitionRepo,
            diagnosticsService: diagnostics);

        sessionService.SetResult("auto-ok", new AgentRunResult
        {
            Success = true,
            Response = "Done.",
            StopReason = StopReason.EndTurn,
        });

        await scheduler.TickAsync();

        diagnostics.Events.Should().Contain(e =>
            e.EventType == "automation.run_succeeded" && e.Level == "info");
    }

    [Fact]
    public async Task RunFailed_should_emit_automation_run_failed_event()
    {
        var definitionRepo = new InMemoryDefinitionRepo();
        await definitionRepo.UpsertAsync(BuildDefinition("auto-fail", nextRunAt: null));

        var diagnostics = new CollectingDiagnosticsService();
        var (scheduler, sessionService) = BuildScheduler(
            definitionRepository: definitionRepo,
            diagnosticsService: diagnostics);

        sessionService.SetResult("auto-fail", new AgentRunResult
        {
            Success = false,
            Response = "Too many tools.",
            StopReason = StopReason.MaxIterations,
        });

        await scheduler.TickAsync();

        diagnostics.Events.Should().Contain(e =>
            e.EventType == "automation.run_failed" && e.Level == "warning");
    }

    [Fact]
    public async Task RunCrashed_should_emit_automation_run_crashed_event()
    {
        var definitionRepo = new InMemoryDefinitionRepo();
        await definitionRepo.UpsertAsync(BuildDefinition("auto-crash", nextRunAt: null));

        var diagnostics = new CollectingDiagnosticsService();
        var (scheduler, sessionService) = BuildScheduler(
            definitionRepository: definitionRepo,
            diagnosticsService: diagnostics);

        sessionService.SetThrows("auto-crash", new InvalidOperationException("session exploded"));

        await scheduler.TickAsync();

        diagnostics.Events.Should().Contain(e =>
            e.EventType == "automation.run_crashed" && e.Level == "error");
    }

    [Fact]
    public async Task SettingsReadFailed_should_emit_automation_settings_read_failed_event()
    {
        var definitionRepo = new InMemoryDefinitionRepo();
        await definitionRepo.UpsertAsync(BuildDefinition("auto-settings", nextRunAt: null));

        var failingSettings = new Mock<ISettingsRepository>();
        failingSettings
            .Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("db error"));

        var diagnostics = new CollectingDiagnosticsService();
        var (scheduler, _) = BuildScheduler(
            definitionRepository: definitionRepo,
            settingsRepository: failingSettings.Object,
            diagnosticsService: diagnostics);

        await scheduler.TickAsync();

        diagnostics.Events.Should().Contain(e =>
            e.EventType == "automation.settings_read_failed" && e.Level == "warning");
    }

    [Fact]
    public async Task RunSucceeded_event_should_contain_automationId_in_message()
    {
        var definitionRepo = new InMemoryDefinitionRepo();
        await definitionRepo.UpsertAsync(BuildDefinition("auto-msg-check", nextRunAt: null));

        var diagnostics = new CollectingDiagnosticsService();
        var (scheduler, sessionService) = BuildScheduler(
            definitionRepository: definitionRepo,
            diagnosticsService: diagnostics);

        sessionService.SetResult("auto-msg-check", new AgentRunResult
        {
            Success = true,
            Response = "ok",
            StopReason = StopReason.EndTurn,
        });

        await scheduler.TickAsync();

        diagnostics.Events.Should().Contain(e =>
            e.EventType == "automation.run_succeeded"
            && e.Message.Contains("auto-msg-check"));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static (IAutomationScheduler Scheduler, ThrowableSessionService SessionService)
        BuildScheduler(
            IAutomationDefinitionRepository? definitionRepository = null,
            ISettingsRepository? settingsRepository = null,
            IDiagnosticsService? diagnosticsService = null,
            bool automationsEnabled = true)
    {
        var now = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeAutomationClock(now);
        var sessionService = new ThrowableSessionService();
        var definitionRepo = definitionRepository ?? new InMemoryDefinitionRepo();
        var runRepo = new InMemoryRunRepo();
        var inboxRepo = new InMemoryInboxRepo();

        ISettingsRepository resolvedSettings;
        if (settingsRepository is not null)
        {
            resolvedSettings = settingsRepository;
        }
        else
        {
            var mock = new Mock<ISettingsRepository>();
            mock.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(KodaClawSettings.Default with { AutomationsEnabled = automationsEnabled });
            resolvedSettings = mock.Object;
        }

        var options = new AutomationSchedulerOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMinutes(1),
            FailureRetryDelay = TimeSpan.FromMinutes(15),
        };

        var scheduler = new AutomationScheduler(
            definitionRepo,
            runRepo,
            sessionService,
            inboxRepo,
            clock,
            options,
            resolvedSettings,
            notificationService: null,
            memoryConsolidationService: null,
            correlationContextAccessor: null,
            diagnosticsService: diagnosticsService);

        return (scheduler, sessionService);
    }

    private static AutomationDefinition BuildDefinition(string id, DateTimeOffset? nextRunAt)
    {
        var now = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        return new AutomationDefinition(
            Id: id,
            Title: $"Automation {id}",
            Prompt: $"Run {id}.",
            Source: AutomationDefinitionSource.Manual,
            SourcePath: null,
            CronExpression: "0 * * * *",
            Enabled: true,
            InputPaths: null,
            ModelId: null,
            NotificationChannels: null,
            NotifyMode: AutomationNotifyMode.None,
            CreatedAt: now,
            UpdatedAt: now,
            LastRunAt: null,
            NextRunAt: nextRunAt,
            LastRunStatus: null,
            LastError: null);
    }

    // -----------------------------------------------------------------------
    // Stubs
    // -----------------------------------------------------------------------

    private sealed class CollectingDiagnosticsService : IDiagnosticsService
    {
        private readonly List<DiagnosticEvent> _events = [];
        public IReadOnlyList<DiagnosticEvent> Events => _events;

        public void Record(DiagnosticEvent diagnosticEvent) => _events.Add(diagnosticEvent);

        public IReadOnlyList<DiagnosticEvent> GetRecent(int limit = 50, string? correlationId = null) => _events;

        public IReadOnlyList<DiagnosticEvent> Query(DiagnosticsQuery? query = null) => _events;

        public DiagnosticsStatsResponse GetStats(DateTimeOffset? since = null) => new(0, 0, 0, [], null, null);

        public Task ClearAsync(DateTimeOffset? before = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<DiagnosticEvent> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ThrowableSessionService : IAutomationSessionService
    {
        private readonly Dictionary<string, AgentRunResult> _results = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> _throws = new(StringComparer.Ordinal);

        public void SetResult(string automationId, AgentRunResult result)
            => _results[automationId] = result;

        public void SetThrows(string automationId, Exception ex)
            => _throws[automationId] = ex;

        public Task<AutomationSessionHandle> StartAutomationSessionAsync(
            AutomationDefinition definition,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_throws.TryGetValue(definition.Id, out var ex))
            {
                return Task.FromException<AutomationSessionHandle>(ex);
            }

            var runResult = _results.TryGetValue(definition.Id, out var configured)
                ? configured
                : new AgentRunResult { Success = true, Response = "ok", StopReason = StopReason.EndTurn };

            var agent = new Mock<IAgent>();
            agent.SetupGet(a => a.AgentId).Returns($"agent-{definition.Id}");
            agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(runResult);

            var handle = new AutomationSessionHandle(
                SessionId: $"session-{definition.Id}",
                AutomationId: definition.Id,
                SessionKind: SessionKind.Automation,
                SessionDirectory: "/tmp",
                Agent: agent.Object);
            return Task.FromResult(handle);
        }

        public Task<AutomationSessionHandle> StartJobSessionAsync(
            JobDefinition job, CancellationToken cancellationToken = default)
            => Task.FromResult<AutomationSessionHandle>(default!);
    }

    private sealed class InMemoryDefinitionRepo : IAutomationDefinitionRepository
    {
        private readonly Dictionary<string, AutomationDefinition> _items = new(StringComparer.Ordinal);

        public Task UpsertAsync(AutomationDefinition definition, CancellationToken _ = default)
        {
            _items[definition.Id] = definition;
            return Task.CompletedTask;
        }

        public Task<AutomationDefinition?> GetByIdAsync(string id, CancellationToken _ = default)
        {
            _items.TryGetValue(id, out var r);
            return Task.FromResult(r);
        }

        public Task<IReadOnlyList<AutomationDefinition>> ListAsync(
            AutomationDefinitionQuery? query = null,
            CancellationToken _ = default)
        {
            IEnumerable<AutomationDefinition> items = _items.Values;
            if (query?.Enabled.HasValue == true)
                items = items.Where(d => d.Enabled == query.Enabled.Value);
            IReadOnlyList<AutomationDefinition> result = items.Take(query?.Limit ?? int.MaxValue).ToList();
            return Task.FromResult(result);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken _ = default)
            => Task.FromResult(_items.Remove(id));
    }

    private sealed class InMemoryRunRepo : IAutomationRunRepository
    {
        private readonly List<AutomationRunRecord> _items = [];

        public Task AddAsync(AutomationRunRecord r, CancellationToken _ = default)
        {
            _items.Add(r);
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(AutomationRunRecord r, CancellationToken _ = default)
        {
            var idx = _items.FindIndex(x => x.RunId == r.RunId);
            if (idx < 0) return Task.FromResult(false);
            _items[idx] = r;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<AutomationRunRecord>> ListAsync(
            AutomationRunQuery? query = null,
            CancellationToken _ = default)
        {
            IEnumerable<AutomationRunRecord> items = _items;
            if (query?.AutomationId is not null)
                items = items.Where(r => r.AutomationId == query.AutomationId);
            if (query?.Status.HasValue == true)
                items = items.Where(r => r.Status == query.Status.Value);
            IReadOnlyList<AutomationRunRecord> result = items
                .OrderByDescending(r => r.StartedAt)
                .Take(query?.Limit ?? int.MaxValue)
                .ToList();
            return Task.FromResult(result);
        }

        public Task<AutomationRunRecord?> GetByIdAsync(string runId, CancellationToken _ = default)
        {
            var r = _items.FirstOrDefault(x => x.RunId == runId);
            return Task.FromResult(r);
        }
    }

    private sealed class InMemoryInboxRepo : IInboxRepository
    {
        private readonly Dictionary<string, InboxItem> _items = new(StringComparer.Ordinal);

        public Task UpsertAsync(InboxItem item, CancellationToken _ = default)
        {
            _items[item.Id] = item;
            return Task.CompletedTask;
        }

        public Task<InboxItem?> GetByIdAsync(string id, CancellationToken _ = default)
        {
            _items.TryGetValue(id, out var r);
            return Task.FromResult(r);
        }

        public Task<IReadOnlyList<InboxItem>> ListAsync(InboxQuery? query = null, CancellationToken _ = default)
        {
            IReadOnlyList<InboxItem> result = _items.Values.ToList();
            return Task.FromResult(result);
        }

        public Task<bool> UpdateStatusAsync(
            string id, InboxItemStatus status, DateTimeOffset updatedAt,
            DateTimeOffset? resolvedAt = null, CancellationToken _ = default)
        {
            if (!_items.TryGetValue(id, out var item)) return Task.FromResult(false);
            _items[id] = item with { Status = status, UpdatedAt = updatedAt, ResolvedAt = resolvedAt };
            return Task.FromResult(true);
        }
    }
}
