using FluentAssertions;
using KodaClaw.Automation;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Workspace;
using KodaClaw.ControlPlane;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Heartbeat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Automation;

public sealed class HeartbeatSyncIntegrationTests : IDisposable
{
    private readonly string _rootPath;
    private readonly ServiceProvider _provider;
    private readonly IHeartbeatSyncService _syncService;
    private readonly IAutomationDefinitionRepository _definitions;

    public HeartbeatSyncIntegrationTests()
    {
        _rootPath = Path.Combine(
            Path.GetTempPath(),
            "kodaclaw-heartbeat-sync-integration",
            Guid.NewGuid().ToString("N"));

        var workspacePath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory);
        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(Path.Combine(_rootPath, KodaClawWorkspaceLayout.ConfigDirectory));

        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = _rootPath);
        services.AddKodaClawJsonStore(_rootPath);
        services.AddKodaClawControlPlane();
        services.AddKodaClawAutomation(options =>
        {
            options.Enabled = false;
        });

        _provider = services.BuildServiceProvider();
        _syncService = _provider.GetRequiredService<IHeartbeatSyncService>();
        _definitions = _provider.GetRequiredService<IAutomationDefinitionRepository>();
    }

    [Fact]
    public async Task Sync_inserts_definitions_from_heartbeat_file()
    {
        WriteHeartbeatFile("""
            ## Daily Digest
            - schedule: daily 09:00
            - prompt: Summarize daily workspace progress.

            ## Hourly Check
            - schedule: hourly 2h
            - prompt: Check for urgent tasks every 2 hours.
            """);

        var result = await _syncService.SyncAsync();

        result.CompilationFailed.Should().BeFalse();
        result.Upserted.Should().Be(2);
        result.Deleted.Should().Be(0);

        var stored = await _definitions.ListAsync(
            new AutomationDefinitionQuery(Source: AutomationDefinitionSource.Heartbeat, Limit: 50));

        stored.Should().HaveCount(2);
        stored.Should().Contain(d => d.CronExpression == "0 9 * * *");
        stored.Should().Contain(d => d.CronExpression == "0 */2 * * *");
    }

    [Fact]
    public async Task Second_sync_preserves_scheduling_state()
    {
        WriteHeartbeatFile("""
            ## Morning Summary
            - schedule: daily 08:00
            - prompt: Summarize overnight changes.
            """);

        await _syncService.SyncAsync();

        var afterFirstSync = await _definitions.ListAsync(
            new AutomationDefinitionQuery(Source: AutomationDefinitionSource.Heartbeat, Limit: 50));
        afterFirstSync.Should().HaveCount(1);

        // Simulate the scheduler setting NextRunAt and LastRunAt
        var lastRunAt = new DateTimeOffset(2026, 3, 20, 8, 0, 0, TimeSpan.Zero);
        var nextRunAt = new DateTimeOffset(2026, 3, 21, 8, 0, 0, TimeSpan.Zero);
        var withRunState = afterFirstSync[0] with
        {
            LastRunAt = lastRunAt,
            NextRunAt = nextRunAt,
            LastRunStatus = AutomationRunStatus.Succeeded,
        };
        await _definitions.UpsertAsync(withRunState);

        // Update HEARTBEAT.md with a modified prompt
        WriteHeartbeatFile("""
            ## Morning Summary
            - schedule: daily 08:00
            - prompt: Summarize overnight changes and include weather.
            """);

        var result = await _syncService.SyncAsync();

        result.CompilationFailed.Should().BeFalse();
        result.Upserted.Should().Be(1);
        result.Deleted.Should().Be(0);

        var afterSecondSync = await _definitions.ListAsync(
            new AutomationDefinitionQuery(Source: AutomationDefinitionSource.Heartbeat, Limit: 50));
        afterSecondSync.Should().HaveCount(1);

        var updated = afterSecondSync[0];
        updated.LastRunAt.Should().Be(lastRunAt, "scheduling state must be preserved after sync");
        updated.NextRunAt.Should().Be(nextRunAt, "NextRunAt must not be reset by sync");
        updated.LastRunStatus.Should().Be(AutomationRunStatus.Succeeded);
        updated.Prompt.Should().Contain("weather", "prompt should reflect the latest HEARTBEAT.md content");
    }

    [Fact]
    public async Task Deleted_section_removes_definition_from_repository()
    {
        WriteHeartbeatFile("""
            ## Task A
            - schedule: daily 09:00
            - prompt: First task.

            ## Task B
            - schedule: hourly 1h
            - prompt: Second task.
            """);

        await _syncService.SyncAsync();

        var afterFirst = await _definitions.ListAsync(
            new AutomationDefinitionQuery(Source: AutomationDefinitionSource.Heartbeat, Limit: 50));
        afterFirst.Should().HaveCount(2);

        // Remove Task B from HEARTBEAT.md
        WriteHeartbeatFile("""
            ## Task A
            - schedule: daily 09:00
            - prompt: First task.
            """);

        var result = await _syncService.SyncAsync();

        result.Upserted.Should().Be(1);
        result.Deleted.Should().Be(1);

        var afterSecond = await _definitions.ListAsync(
            new AutomationDefinitionQuery(Source: AutomationDefinitionSource.Heartbeat, Limit: 50));
        afterSecond.Should().HaveCount(1);
        afterSecond[0].Prompt.Should().Contain("First task");
    }

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private void WriteHeartbeatFile(string content)
    {
        var path = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.HeartbeatFile);
        File.WriteAllText(path, content);
    }
}
