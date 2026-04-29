using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Storage.Json.Repositories;
using Xunit;

namespace KodaClaw.UnitTests.PluginHost;

public sealed class JsonPluginLogRepositoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonPluginLogRepository CreateRepository() => new(_tempDir);

    [Fact]
    public async Task List_should_start_empty()
    {
        var repository = CreateRepository();

        var entries = await repository.ListAsync("plugin.todo");

        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Append_and_list_should_return_most_recent_entries_and_respect_limit()
    {
        var repository = CreateRepository();
        var baseTime = new DateTimeOffset(2026, 3, 19, 2, 0, 0, TimeSpan.Zero);

        await repository.AppendAsync(new PluginLogEntry(
            EntryId: "log-1",
            PluginId: "plugin.todo",
            Level: "INFO",
            Source: "stdio",
            Message: "first",
            Timestamp: baseTime));
        await repository.AppendAsync(new PluginLogEntry(
            EntryId: "log-2",
            PluginId: "plugin.todo",
            Level: "WARN",
            Source: "stdio",
            Message: "second",
            Timestamp: baseTime.AddSeconds(1)));
        await repository.AppendAsync(new PluginLogEntry(
            EntryId: "log-3",
            PluginId: "plugin.todo",
            Level: "ERROR",
            Source: "stdio",
            Message: "third",
            Timestamp: baseTime.AddSeconds(2)));

        var entries = await repository.ListAsync("plugin.todo", limit: 2);

        // ReadLastLinesAsync reads last N lines and returns in chronological order
        entries.Should().HaveCount(2);
        entries[0].EntryId.Should().Be("log-2");
        entries[1].EntryId.Should().Be("log-3");
    }

    [Fact]
    public async Task List_should_not_return_entries_for_other_plugins()
    {
        var repository = CreateRepository();
        var now = new DateTimeOffset(2026, 3, 19, 2, 30, 0, TimeSpan.Zero);

        await repository.AppendAsync(new PluginLogEntry(
            EntryId: "todo-1",
            PluginId: "plugin.todo",
            Level: "INFO",
            Source: "stdio",
            Message: "todo",
            Timestamp: now));
        await repository.AppendAsync(new PluginLogEntry(
            EntryId: "other-1",
            PluginId: "plugin.other",
            Level: "INFO",
            Source: "stdio",
            Message: "other",
            Timestamp: now.AddSeconds(1)));

        var entries = await repository.ListAsync("plugin.todo");

        entries.Should().ContainSingle();
        entries[0].EntryId.Should().Be("todo-1");
        entries[0].PluginId.Should().Be("plugin.todo");
    }

    [Fact]
    public async Task Append_should_round_trip_payload_json()
    {
        var repository = CreateRepository();
        var now = new DateTimeOffset(2026, 3, 19, 3, 0, 0, TimeSpan.Zero);

        await repository.AppendAsync(new PluginLogEntry(
            EntryId: "payload-1",
            PluginId: "plugin.todo",
            Level: "INFO",
            Source: "tool",
            Message: "hello",
            Timestamp: now,
            PayloadJson: """{"foo":"bar","n":1}"""));

        var entries = await repository.ListAsync("plugin.todo");

        entries.Should().ContainSingle();
        entries[0].PayloadJson.Should().Be("""{"foo":"bar","n":1}""");
    }

    [Fact]
    public async Task Repository_should_persist_log_entries_to_filesystem()
    {
        var repository = CreateRepository();
        await repository.AppendAsync(new PluginLogEntry(
            EntryId: "table-init-1",
            PluginId: "plugin.table-init",
            Level: "INFO",
            Source: "stdio",
            Message: "init",
            Timestamp: DateTimeOffset.UtcNow));

        var entries = await repository.ListAsync("plugin.table-init");
        entries.Should().ContainSingle().Which.EntryId.Should().Be("table-init-1");
    }

}
