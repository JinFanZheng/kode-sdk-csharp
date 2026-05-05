using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using KodaClaw.Automation;
using KodaClaw.Automation.Scheduler;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Jobs;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Timers;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Automation;

public sealed class OneShotTimerServiceTests
{
    [Fact]
    public async Task TickAsync_should_be_noop_when_no_pending_timers()
    {
        var (service, timerRepo, _, _) = BuildService();

        await service.TickAsync();

        timerRepo.AllTimers.Should().BeEmpty();
    }

    [Fact]
    public async Task TickAsync_should_fire_pending_timer_that_is_due()
    {
        var now = new DateTimeOffset(2026, 3, 28, 10, 0, 0, TimeSpan.Zero);
        var (service, timerRepo, sessionService, _) = BuildService(utcNow: now);

        var timer = BuildTimer("t1", fireAt: now.AddMinutes(-5));
        await timerRepo.AddAsync(timer);

        await service.TickAsync();

        // Timer should be marked Fired
        var stored = timerRepo.GetById("t1");
        stored!.Status.Should().Be(OneShotTimerStatus.Fired);
        stored.FiredAt.Should().NotBeNull();

        // Session should have been started with oneshot- prefix
        sessionService.GetStartCount("oneshot-t1").Should().Be(1);
    }

    [Fact]
    public async Task TickAsync_should_skip_timer_not_yet_due()
    {
        var now = new DateTimeOffset(2026, 3, 28, 10, 0, 0, TimeSpan.Zero);
        var (service, timerRepo, sessionService, _) = BuildService(utcNow: now);

        var timer = BuildTimer("t2", fireAt: now.AddMinutes(30));
        await timerRepo.AddAsync(timer);

        await service.TickAsync();

        var stored = timerRepo.GetById("t2");
        stored!.Status.Should().Be(OneShotTimerStatus.Pending);
        sessionService.GetStartCount("oneshot-t2").Should().Be(0);
    }

    [Fact]
    public async Task TickAsync_should_skip_already_fired_timer()
    {
        var now = new DateTimeOffset(2026, 3, 28, 10, 0, 0, TimeSpan.Zero);
        var (service, timerRepo, sessionService, _) = BuildService(utcNow: now);

        var timer = BuildTimer("t3", fireAt: now.AddMinutes(-5)) with
        {
            Status = OneShotTimerStatus.Fired,
            FiredAt = now.AddMinutes(-4),
        };
        await timerRepo.AddAsync(timer);

        await service.TickAsync();

        sessionService.GetStartCount("oneshot-t3").Should().Be(0);
    }

    [Fact]
    public async Task TickAsync_should_write_inbox_item_after_successful_fire()
    {
        var now = new DateTimeOffset(2026, 3, 28, 10, 0, 0, TimeSpan.Zero);
        var (service, timerRepo, _, inboxRepo) = BuildService(utcNow: now);

        var timer = BuildTimer("t4", fireAt: now.AddMinutes(-1));
        await timerRepo.AddAsync(timer);

        await service.TickAsync();

        inboxRepo.AllItems.Should().ContainSingle(i => i.CorrelationId == "t4");
    }

    [Fact]
    public async Task TickAsync_should_fire_multiple_pending_timers_in_order()
    {
        var now = new DateTimeOffset(2026, 3, 28, 10, 0, 0, TimeSpan.Zero);
        var (service, timerRepo, sessionService, _) = BuildService(utcNow: now);

        await timerRepo.AddAsync(BuildTimer("ta", fireAt: now.AddMinutes(-10)));
        await timerRepo.AddAsync(BuildTimer("tb", fireAt: now.AddMinutes(-5)));
        await timerRepo.AddAsync(BuildTimer("tc", fireAt: now.AddMinutes(5)));  // not yet due

        await service.TickAsync();

        sessionService.GetStartCount("oneshot-ta").Should().Be(1);
        sessionService.GetStartCount("oneshot-tb").Should().Be(1);
        sessionService.GetStartCount("oneshot-tc").Should().Be(0);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static OneShotTimerRecord BuildTimer(string id, DateTimeOffset fireAt)
    {
        var now = new DateTimeOffset(2026, 3, 28, 9, 0, 0, TimeSpan.Zero);
        return new OneShotTimerRecord(
            Id: id,
            Title: $"Reminder {id}",
            Prompt: $"Do something for {id}.",
            FireAt: fireAt,
            Status: OneShotTimerStatus.Pending,
            Channels: null,
            CreatedAt: now,
            FiredAt: null,
            ErrorMessage: null);
    }

    private static (
        OneShotTimerService Service,
        InMemoryOneShotTimerRepository TimerRepo,
        StubAutomationSessionService SessionService,
        InMemoryInboxRepository InboxRepo)
        BuildService(DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? new DateTimeOffset(2026, 3, 28, 10, 0, 0, TimeSpan.Zero);
        var clock = new FakeAutomationClock(now);
        var timerRepo = new InMemoryOneShotTimerRepository();
        var sessionService = new StubAutomationSessionService();
        var inboxRepo = new InMemoryInboxRepository();

        var service = new OneShotTimerService(
            timerRepo,
            sessionService,
            inboxRepo,
            clock);

        return (service, timerRepo, sessionService, inboxRepo);
    }

    // -----------------------------------------------------------------------
    // In-memory stubs
    // -----------------------------------------------------------------------

    private sealed class InMemoryOneShotTimerRepository : IOneShotTimerRepository
    {
        private readonly List<OneShotTimerRecord> _items = [];

        public IReadOnlyList<OneShotTimerRecord> AllTimers => _items.AsReadOnly();

        public OneShotTimerRecord? GetById(string id) => _items.FirstOrDefault(t => t.Id == id);

        public Task<OneShotTimerRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(GetById(id));

        public Task<IReadOnlyList<OneShotTimerRecord>> ListPendingAsync(
            DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<OneShotTimerRecord> result = _items
                .Where(t => t.Status == OneShotTimerStatus.Pending && t.FireAt <= now)
                .OrderBy(t => t.FireAt)
                .ToList();
            return Task.FromResult(result);
        }

        public Task AddAsync(OneShotTimerRecord record, CancellationToken cancellationToken = default)
        {
            _items.Add(record);
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(OneShotTimerRecord record, CancellationToken cancellationToken = default)
        {
            var index = _items.FindIndex(t => t.Id == record.Id);
            if (index < 0) return Task.FromResult(false);
            _items[index] = record;
            return Task.FromResult(true);
        }
    }

    private sealed class InMemoryInboxRepository : IInboxRepository
    {
        private readonly List<InboxItem> _items = [];

        public IReadOnlyList<InboxItem> AllItems => _items.AsReadOnly();

        public Task UpsertAsync(InboxItem item, CancellationToken cancellationToken = default)
        {
            var index = _items.FindIndex(i => i.Id == item.Id);
            if (index < 0) _items.Add(item);
            else _items[index] = item;
            return Task.CompletedTask;
        }

        public Task<InboxItem?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.FirstOrDefault(i => i.Id == id));

        public Task<IReadOnlyList<InboxItem>> ListAsync(
            InboxQuery? query = null, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<InboxItem> result = _items.ToList();
            return Task.FromResult(result);
        }

        public Task<bool> UpdateStatusAsync(
            string id, InboxItemStatus status, DateTimeOffset updatedAt,
            DateTimeOffset? resolvedAt = null, CancellationToken cancellationToken = default)
        {
            var index = _items.FindIndex(i => i.Id == id);
            if (index < 0) return Task.FromResult(false);
            _items[index] = _items[index] with { Status = status, UpdatedAt = updatedAt, ResolvedAt = resolvedAt };
            return Task.FromResult(true);
        }
    }

    private sealed class StubAutomationSessionService : IAutomationSessionService
    {
        private readonly Dictionary<string, int> _startCounts = new(StringComparer.Ordinal);

        public int GetStartCount(string automationId)
            => _startCounts.TryGetValue(automationId, out var c) ? c : 0;

        public Task<AutomationSessionHandle> StartAutomationSessionAsync(
            AutomationDefinition definition,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _startCounts[definition.Id] = GetStartCount(definition.Id) + 1;

            var agent = new Mock<IAgent>();
            agent.SetupGet(a => a.AgentId).Returns($"agent-{definition.Id}");
            agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AgentRunResult { Success = true, Response = "ok", StopReason = StopReason.EndTurn });

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

    private sealed class FakeAutomationClock : IAutomationClock
    {
        private readonly DateTimeOffset _now;

        public FakeAutomationClock(DateTimeOffset now) => _now = now;

        public DateTimeOffset UtcNow => _now;
    }
}
