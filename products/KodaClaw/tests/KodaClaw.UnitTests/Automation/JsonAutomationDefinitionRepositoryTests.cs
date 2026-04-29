using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Storage.Json.Repositories;
using Xunit;

namespace KodaClaw.UnitTests.Automation;

public sealed class JsonAutomationDefinitionRepositoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonAutomationDefinitionRepository CreateRepository() => new(_tempDir);

    [Fact]
    public async Task List_should_start_empty()
    {
        var repository = CreateRepository();

        var list = await repository.ListAsync();

        list.Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_should_round_trip_definition()
    {
        var repository = CreateRepository();
        var expected = new AutomationDefinition(
            Id: "auto-weekly-001",
            Title: "Weekly Digest",
            Prompt: "Summarize the workspace changes.",
            Source: AutomationDefinitionSource.Heartbeat,
            SourcePath: "workspace/HEARTBEAT.md",
            CronExpression: "30 9 * * 1,3,5",
            Enabled: true,
            InputPaths: new[] { "docs/roadmap.md", "docs/status.md" },
            ModelId: null,
            NotificationChannels: null,
            NotifyMode: AutomationNotifyMode.None,
            CreatedAt: new DateTimeOffset(2026, 3, 18, 9, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 18, 9, 10, 0, TimeSpan.Zero),
            LastRunAt: new DateTimeOffset(2026, 3, 18, 1, 0, 0, TimeSpan.Zero),
            NextRunAt: new DateTimeOffset(2026, 3, 20, 1, 30, 0, TimeSpan.Zero),
            LastRunStatus: AutomationRunStatus.Succeeded,
            LastError: null);

        await repository.UpsertAsync(expected);
        var actual = await repository.GetByIdAsync(expected.Id);

        actual.Should().NotBeNull();
        actual!.Id.Should().Be(expected.Id);
        actual.Title.Should().Be(expected.Title);
        actual.Prompt.Should().Be(expected.Prompt);
        actual.Source.Should().Be(expected.Source);
        actual.SourcePath.Should().Be(expected.SourcePath);
        actual.Enabled.Should().Be(expected.Enabled);
        actual.CronExpression.Should().Be(expected.CronExpression);
        actual.InputPaths.Should().Equal(expected.InputPaths!);
        actual.CreatedAt.Should().Be(expected.CreatedAt);
        actual.UpdatedAt.Should().Be(expected.UpdatedAt);
        actual.LastRunAt.Should().Be(expected.LastRunAt);
        actual.NextRunAt.Should().Be(expected.NextRunAt);
        actual.LastRunStatus.Should().Be(expected.LastRunStatus);
        actual.LastError.Should().BeNull();
    }

    [Fact]
    public async Task List_should_filter_by_enabled_and_source()
    {
        var repository = CreateRepository();
        var now = new DateTimeOffset(2026, 3, 18, 10, 0, 0, TimeSpan.Zero);

        await repository.UpsertAsync(BuildDefinition("auto-001", AutomationDefinitionSource.Heartbeat, enabled: true, now));
        await repository.UpsertAsync(BuildDefinition("auto-002", AutomationDefinitionSource.Heartbeat, enabled: false, now.AddMinutes(1)));
        await repository.UpsertAsync(BuildDefinition("auto-003", AutomationDefinitionSource.Manual, enabled: true, now.AddMinutes(2)));

        var filtered = await repository.ListAsync(new AutomationDefinitionQuery(
            Enabled: true,
            Source: AutomationDefinitionSource.Heartbeat,
            Limit: 20));

        filtered.Should().HaveCount(1);
        filtered[0].Id.Should().Be("auto-001");
    }

    private static AutomationDefinition BuildDefinition(
        string id,
        AutomationDefinitionSource source,
        bool enabled,
        DateTimeOffset now)
    {
        return new AutomationDefinition(
            Id: id,
            Title: $"Automation {id}",
            Prompt: "Run scheduled workspace check.",
            Source: source,
            SourcePath: source == AutomationDefinitionSource.Heartbeat ? "workspace/HEARTBEAT.md" : null,
            CronExpression: "0 9 * * *",
            Enabled: enabled,
            InputPaths: new[] { "docs/notes.md" },
            ModelId: null,
            NotificationChannels: null,
            NotifyMode: AutomationNotifyMode.None,
            CreatedAt: now,
            UpdatedAt: now,
            LastRunAt: null,
            NextRunAt: null,
            LastRunStatus: null,
            LastError: null);
    }
}
