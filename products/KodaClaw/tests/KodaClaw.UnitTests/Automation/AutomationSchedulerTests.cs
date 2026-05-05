using FluentAssertions;
using KodaClaw.Automation;
using KodaClaw.Automation.Scheduler;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Jobs;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Settings;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Automation;

public sealed class AutomationSchedulerTests
{
[Fact]
    public async Task TickAsync_should_be_noop_when_AutomationsEnabled_is_false()
    {
        var now = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        var definitionRepo = new InMemoryAutomationDefinitionRepository();
        await definitionRepo.UpsertAsync(BuildDefinition("auto-disabled", nextRunAt: null));

        var (scheduler, sessionService, _, _, _) = BuildScheduler(
            automationsEnabled: false,
            utcNow: now,
            definitionRepository: definitionRepo);

        var executed = await scheduler.TickAsync();

        executed.Should().Be(0);
        sessionService.GetStartCount("auto-disabled").Should().Be(0);
    }

    [Fact]
    public async Task TickAsync_should_execute_due_automations_when_AutomationsEnabled_is_true()
    {
        var now = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        var definitionRepo = new InMemoryAutomationDefinitionRepository();
        await definitionRepo.UpsertAsync(BuildDefinition("auto-enabled", nextRunAt: null));

        var (scheduler, sessionService, _, _, _) = BuildScheduler(
            automationsEnabled: true,
            utcNow: now,
            definitionRepository: definitionRepo);

        sessionService.SetResult("auto-enabled", new AgentRunResult
        {
            Success = true,
            Response = "Automation ran.",
            StopReason = StopReason.EndTurn,
        });

        var executed = await scheduler.TickAsync();

        executed.Should().Be(1);
        sessionService.GetStartCount("auto-enabled").Should().Be(1);
    }

    [Fact]
    public async Task TickAsync_should_be_noop_when_settings_repository_throws()
    {
        var now = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        var definitionRepo = new InMemoryAutomationDefinitionRepository();
        await definitionRepo.UpsertAsync(BuildDefinition("auto-failing-settings", nextRunAt: null));

        var failingSettings = new Mock<ISettingsRepository>();
        failingSettings
            .Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("settings DB unavailable"));

        var (scheduler, sessionService, _, _, _) = BuildScheduler(
            utcNow: now,
            definitionRepository: definitionRepo,
            settingsRepository: failingSettings.Object);

        var executed = await scheduler.TickAsync();

        executed.Should().Be(0);
        sessionService.GetStartCount("auto-failing-settings").Should().Be(0);
    }

    [Fact]
    public async Task RunOnceAsync_should_ignore_AutomationsEnabled_gate_and_execute()
    {
        var now = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        var definitionRepo = new InMemoryAutomationDefinitionRepository();
        await definitionRepo.UpsertAsync(BuildDefinition("auto-run-once", nextRunAt: null));

        var (scheduler, sessionService, _, _, _) = BuildScheduler(
            automationsEnabled: false,
            utcNow: now,
            definitionRepository: definitionRepo);

        sessionService.SetResult("auto-run-once", new AgentRunResult
        {
            Success = true,
            Response = "Force ran.",
            StopReason = StopReason.EndTurn,
        });

        var executed = await scheduler.RunOnceAsync();

        executed.Should().Be(1);
        sessionService.GetStartCount("auto-run-once").Should().Be(1);
    }

    [Fact]
    public async Task TickAsync_should_be_noop_when_options_Enabled_is_false_regardless_of_settings()
    {
        var now = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        var definitionRepo = new InMemoryAutomationDefinitionRepository();
        await definitionRepo.UpsertAsync(BuildDefinition("auto-options-disabled", nextRunAt: null));

        var (scheduler, sessionService, _, _, _) = BuildScheduler(
            automationsEnabled: true,
            optionsEnabled: false,
            utcNow: now,
            definitionRepository: definitionRepo);

        var executed = await scheduler.TickAsync();

        executed.Should().Be(0);
        sessionService.GetStartCount("auto-options-disabled").Should().Be(0);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static (
        IAutomationScheduler Scheduler,
        StubAutomationSessionService SessionService,
        IAutomationDefinitionRepository DefinitionRepository,
        IAutomationRunRepository RunRepository,
        IInboxRepository InboxRepository)
        BuildScheduler(
            bool automationsEnabled = true,
            bool optionsEnabled = true,
            DateTimeOffset? utcNow = null,
            IAutomationDefinitionRepository? definitionRepository = null,
            ISettingsRepository? settingsRepository = null)
    {
        var now = utcNow ?? new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeAutomationClock(now);
        var sessionService = new StubAutomationSessionService();
        var definitionRepo = definitionRepository ?? new InMemoryAutomationDefinitionRepository();
        var runRepo = new InMemoryAutomationRunRepository();
        var inboxRepo = new InMemoryInboxRepository();

        ISettingsRepository? resolvedSettings = settingsRepository;
        if (resolvedSettings is null)
        {
            var mock = new Mock<ISettingsRepository>();
            mock.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(KodaClawSettings.Default with { AutomationsEnabled = automationsEnabled });
            resolvedSettings = mock.Object;
        }

        var options = new AutomationSchedulerOptions
        {
            Enabled = optionsEnabled,
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
            logger: NullLogger<AutomationScheduler>.Instance);

        return (scheduler, sessionService, definitionRepo, runRepo, inboxRepo);
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
    // In-memory stubs
    // -----------------------------------------------------------------------

    private sealed class InMemoryAutomationDefinitionRepository : IAutomationDefinitionRepository
    {
        private readonly Dictionary<string, AutomationDefinition> _items = new(StringComparer.Ordinal);

        public Task UpsertAsync(AutomationDefinition definition, CancellationToken cancellationToken = default)
        {
            _items[definition.Id] = definition;
            return Task.CompletedTask;
        }

        public Task<AutomationDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            _items.TryGetValue(id, out var result);
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<AutomationDefinition>> ListAsync(
            AutomationDefinitionQuery? query = null,
            CancellationToken cancellationToken = default)
        {
            IEnumerable<AutomationDefinition> items = _items.Values;
            if (query?.Enabled.HasValue == true)
            {
                items = items.Where(d => d.Enabled == query.Enabled.Value);
            }

            if (query?.Source.HasValue == true)
            {
                items = items.Where(d => d.Source == query.Source.Value);
            }

            var limit = query?.Limit ?? 50;
            IReadOnlyList<AutomationDefinition> result = items.Take(limit).ToList();
            return Task.FromResult(result);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_items.Remove(id));
        }
    }

    private sealed class InMemoryAutomationRunRepository : IAutomationRunRepository
    {
        private readonly List<AutomationRunRecord> _items = [];

        public Task AddAsync(AutomationRunRecord runRecord, CancellationToken cancellationToken = default)
        {
            _items.Add(runRecord);
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(AutomationRunRecord runRecord, CancellationToken cancellationToken = default)
        {
            var index = _items.FindIndex(r => r.RunId == runRecord.RunId);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            _items[index] = runRecord;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<AutomationRunRecord>> ListAsync(
            AutomationRunQuery? query = null,
            CancellationToken cancellationToken = default)
        {
            IEnumerable<AutomationRunRecord> items = _items;
            if (query?.AutomationId is not null)
            {
                items = items.Where(r => r.AutomationId == query.AutomationId);
            }

            if (query?.Status.HasValue == true)
            {
                items = items.Where(r => r.Status == query.Status.Value);
            }

            var limit = query?.Limit ?? 50;
            IReadOnlyList<AutomationRunRecord> result = items
                .OrderByDescending(r => r.StartedAt)
                .Take(limit)
                .ToList();
            return Task.FromResult(result);
        }

        public Task<AutomationRunRecord?> GetByIdAsync(string runId, CancellationToken cancellationToken = default)
        {
            var result = _items.FirstOrDefault(r => r.RunId == runId);
            return Task.FromResult(result);
        }
    }

    private sealed class InMemoryInboxRepository : IInboxRepository
    {
        private readonly Dictionary<string, InboxItem> _items = new(StringComparer.Ordinal);

        public Task UpsertAsync(InboxItem item, CancellationToken cancellationToken = default)
        {
            _items[item.Id] = item;
            return Task.CompletedTask;
        }

        public Task<InboxItem?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            _items.TryGetValue(id, out var result);
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<InboxItem>> ListAsync(
            InboxQuery? query = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<InboxItem> result = _items.Values.ToList();
            return Task.FromResult(result);
        }

        public Task<bool> UpdateStatusAsync(
            string id,
            InboxItemStatus status,
            DateTimeOffset updatedAt,
            DateTimeOffset? resolvedAt = null,
            CancellationToken cancellationToken = default)
        {
            if (!_items.TryGetValue(id, out var item))
            {
                return Task.FromResult(false);
            }

            _items[id] = item with { Status = status, UpdatedAt = updatedAt, ResolvedAt = resolvedAt };
            return Task.FromResult(true);
        }
    }

    private sealed class StubAutomationSessionService : IAutomationSessionService
    {
        private readonly Dictionary<string, AgentRunResult> _results = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _startCounts = new(StringComparer.Ordinal);

        public void SetResult(string automationId, AgentRunResult result)
        {
            _results[automationId] = result;
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
}
