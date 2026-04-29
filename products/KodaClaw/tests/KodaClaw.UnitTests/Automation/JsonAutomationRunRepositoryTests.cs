using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Storage.Json.Repositories;
using Xunit;

namespace KodaClaw.UnitTests.Automation;

public sealed class JsonAutomationRunRepositoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonAutomationRunRepository CreateRepository() => new(_tempDir);

    [Fact]
    public async Task List_should_start_empty()
    {
        var repository = CreateRepository();

        var list = await repository.ListAsync();

        list.Should().BeEmpty();
    }

    [Fact]
    public async Task Add_and_get_should_round_trip_run_record()
    {
        var repository = CreateRepository();
        var expected = BuildRunRecord(
            runId: "run-001",
            automationId: "daily-inbox-digest",
            status: AutomationRunStatus.Running,
            startedAt: new DateTimeOffset(2026, 3, 18, 9, 0, 0, TimeSpan.Zero)) with
        {
            SessionId = "auto-20260318090000-daily-abcd1234",
        };

        await repository.AddAsync(expected);
        var actual = await repository.GetByIdAsync(expected.RunId);

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task Update_should_persist_terminal_fields()
    {
        var repository = CreateRepository();
        var startedAt = new DateTimeOffset(2026, 3, 18, 9, 0, 0, TimeSpan.Zero);
        var runRecord = BuildRunRecord(
            runId: "run-002",
            automationId: "daily-inbox-digest",
            status: AutomationRunStatus.Running,
            startedAt: startedAt);

        await repository.AddAsync(runRecord);

        var updated = runRecord with
        {
            Status = AutomationRunStatus.Failed,
            CompletedAt = startedAt.AddMinutes(3),
            Summary = "Digest generation failed.",
            ErrorMessage = "Model timeout.",
        };

        var didUpdate = await repository.UpdateAsync(updated);
        var reloaded = await repository.GetByIdAsync(updated.RunId);

        didUpdate.Should().BeTrue();
        reloaded.Should().Be(updated);
    }

    [Fact]
    public async Task List_should_filter_by_automation_id_and_status()
    {
        var repository = CreateRepository();
        var startedAt = new DateTimeOffset(2026, 3, 18, 9, 0, 0, TimeSpan.Zero);

        await repository.AddAsync(BuildRunRecord("run-a", "daily-inbox-digest", AutomationRunStatus.Succeeded, startedAt));
        await repository.AddAsync(BuildRunRecord("run-b", "daily-inbox-digest", AutomationRunStatus.Failed, startedAt.AddMinutes(1)));
        await repository.AddAsync(BuildRunRecord("run-c", "weekly-review", AutomationRunStatus.Failed, startedAt.AddMinutes(2)));

        var filtered = await repository.ListAsync(new AutomationRunQuery(
            AutomationId: "daily-inbox-digest",
            Status: AutomationRunStatus.Failed,
            Limit: 10));

        filtered.Should().HaveCount(1);
        filtered[0].RunId.Should().Be("run-b");
    }

    private static AutomationRunRecord BuildRunRecord(
        string runId,
        string automationId,
        AutomationRunStatus status,
        DateTimeOffset startedAt)
    {
        return new AutomationRunRecord(
            RunId: runId,
            AutomationId: automationId,
            Status: status,
            Trigger: "heartbeat",
            Attempt: 1,
            SessionId: null,
            StartedAt: startedAt,
            CompletedAt: null,
            Summary: null,
            ErrorMessage: null);
    }
}
