using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Heartbeat;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Workspace;

public sealed class HeartbeatSyncServiceTests : IDisposable
{
    private readonly string _rootPath;
    private readonly Mock<IHeartbeatAutomationCompiler> _compiler;
    private readonly Mock<IAutomationDefinitionRepository> _repository;
    private readonly IHeartbeatSyncService _syncService;

    public HeartbeatSyncServiceTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-heartbeat-sync-unit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory));

        _compiler = new Mock<IHeartbeatAutomationCompiler>();
        _repository = new Mock<IAutomationDefinitionRepository>();

        var workspaceService = new StubWorkspaceService(_rootPath);
        _syncService = new HeartbeatSyncService(workspaceService, _compiler.Object, _repository.Object);
    }

    [Fact]
    public async Task New_definitions_are_upserted_into_repository()
    {
        var markdown = BuildMarkdown("Daily Report");
        WriteHeartbeatFile(markdown);

        var compiled = new[]
        {
            BuildDefinition("daily-report"),
        };
        _compiler.Setup(c => c.Compile(markdown)).Returns(compiled);
        _repository
            .Setup(r => r.ListAsync(It.IsAny<AutomationDefinitionQuery?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AutomationDefinition>());
        _repository
            .Setup(r => r.UpsertAsync(It.IsAny<AutomationDefinition>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _syncService.SyncAsync();

        result.Upserted.Should().Be(1);
        result.Deleted.Should().Be(0);
        result.CompilationFailed.Should().BeFalse();
        _repository.Verify(r => r.UpsertAsync(
            It.Is<AutomationDefinition>(d => d.Id == "daily-report"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Modified_definition_preserves_NextRunAt_and_LastRunAt()
    {
        var markdown = BuildMarkdown("Daily Report");
        WriteHeartbeatFile(markdown);

        var lastRunAt = new DateTimeOffset(2026, 3, 19, 8, 0, 0, TimeSpan.Zero);
        var nextRunAt = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);

        var existing = BuildDefinition("daily-report") with
        {
            CreatedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            LastRunAt = lastRunAt,
            NextRunAt = nextRunAt,
            LastRunStatus = AutomationRunStatus.Succeeded,
            LastError = null,
        };

        var compiled = new[] { BuildDefinition("daily-report") with { Prompt = "Updated prompt." } };

        _compiler.Setup(c => c.Compile(markdown)).Returns(compiled);
        _repository
            .Setup(r => r.ListAsync(It.IsAny<AutomationDefinitionQuery?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { existing });

        AutomationDefinition? upserted = null;
        _repository
            .Setup(r => r.UpsertAsync(It.IsAny<AutomationDefinition>(), It.IsAny<CancellationToken>()))
            .Callback<AutomationDefinition, CancellationToken>((d, _) => upserted = d)
            .Returns(Task.CompletedTask);

        var result = await _syncService.SyncAsync();

        result.Upserted.Should().Be(1);
        result.Deleted.Should().Be(0);
        upserted.Should().NotBeNull();
        upserted!.LastRunAt.Should().Be(lastRunAt);
        upserted.NextRunAt.Should().Be(nextRunAt);
        upserted.LastRunStatus.Should().Be(AutomationRunStatus.Succeeded);
        upserted.CreatedAt.Should().Be(existing.CreatedAt);
        upserted.Prompt.Should().Be("Updated prompt.");
    }

    [Fact]
    public async Task Removed_section_causes_definition_to_be_deleted()
    {
        var markdown = BuildMarkdown("Daily Report");
        WriteHeartbeatFile(markdown);

        var toDelete = BuildDefinition("old-automation");
        var toKeep = BuildDefinition("daily-report");

        _compiler.Setup(c => c.Compile(markdown)).Returns(new[] { toKeep });
        _repository
            .Setup(r => r.ListAsync(It.IsAny<AutomationDefinitionQuery?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { toKeep, toDelete });
        _repository
            .Setup(r => r.UpsertAsync(It.IsAny<AutomationDefinition>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repository
            .Setup(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _syncService.SyncAsync();

        result.Upserted.Should().Be(1);
        result.Deleted.Should().Be(1);
        _repository.Verify(r => r.DeleteAsync("old-automation", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Compilation_failure_leaves_existing_definitions_unchanged_and_returns_failed()
    {
        var markdown = BuildMarkdown("Bad Section");
        WriteHeartbeatFile(markdown);

        _compiler.Setup(c => c.Compile(markdown))
            .Throws(new HeartbeatCompilationException("Invalid schedule expression", lineNumber: 3));

        var result = await _syncService.SyncAsync();

        result.CompilationFailed.Should().BeTrue();
        result.Upserted.Should().Be(0);
        result.Deleted.Should().Be(0);
        _repository.Verify(
            r => r.UpsertAsync(It.IsAny<AutomationDefinition>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repository.Verify(
            r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Empty_file_deletes_all_existing_definitions()
    {
        WriteHeartbeatFile("   ");  // 存在但内容为空白

        var existing = BuildDefinition("daily-report");
        _repository
            .Setup(r => r.ListAsync(It.IsAny<AutomationDefinitionQuery?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { existing });
        _repository
            .Setup(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _syncService.SyncAsync();

        result.CompilationFailed.Should().BeFalse();
        result.Upserted.Should().Be(0);
        result.Deleted.Should().Be(1);
        _repository.Verify(r => r.DeleteAsync("daily-report", It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(
            r => r.UpsertAsync(It.IsAny<AutomationDefinition>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Missing_file_returns_empty_result_without_touching_repository()
    {
        // No file written — HEARTBEAT.md does not exist.
        var result = await _syncService.SyncAsync();

        result.Upserted.Should().Be(0);
        result.Deleted.Should().Be(0);
        result.CompilationFailed.Should().BeFalse();
        _repository.Verify(
            r => r.UpsertAsync(It.IsAny<AutomationDefinition>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _repository.Verify(
            r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task New_definition_gets_NextRunAt_computed_from_cron_so_it_does_not_fire_immediately()
    {
        // Regression: new definitions previously got NextRunAt=null which caused the scheduler
        // to treat them as immediately due (IsDue returns true when NextRunAt is null).
        var markdown = BuildMarkdown("Daily Report");
        WriteHeartbeatFile(markdown);

        var compiled = new[] { BuildDefinition("daily-report") };  // NextRunAt = null from compiler
        _compiler.Setup(c => c.Compile(markdown)).Returns(compiled);
        _repository
            .Setup(r => r.ListAsync(It.IsAny<AutomationDefinitionQuery?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AutomationDefinition>());  // no existing → brand new

        AutomationDefinition? upserted = null;
        _repository
            .Setup(r => r.UpsertAsync(It.IsAny<AutomationDefinition>(), It.IsAny<CancellationToken>()))
            .Callback<AutomationDefinition, CancellationToken>((d, _) => upserted = d)
            .Returns(Task.CompletedTask);

        var before = DateTimeOffset.UtcNow;
        await _syncService.SyncAsync();
        var after = DateTimeOffset.UtcNow;

        upserted.Should().NotBeNull();
        upserted!.NextRunAt.Should().NotBeNull("new definitions must have NextRunAt computed from cron to avoid immediate firing");
        upserted.NextRunAt.Should().BeAfter(before, "NextRunAt must be in the future, not in the past");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private void WriteHeartbeatFile(string content)
    {
        var path = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.HeartbeatFile);
        File.WriteAllText(path, content);
    }

    private static string BuildMarkdown(string sectionTitle) =>
        $"## {sectionTitle}\n\nschedule: daily 09:00\n\nSummarize daily progress.\n";

    private static AutomationDefinition BuildDefinition(string id) =>
        new AutomationDefinition(
            Id: id,
            Title: "Daily Report",
            Prompt: "Summarize daily progress.",
            Source: AutomationDefinitionSource.Heartbeat,
            SourcePath: "workspace/HEARTBEAT.md",
            CronExpression: "0 * * * *",
            Enabled: true,
            InputPaths: null,
            ModelId: null,
            NotificationChannels: null,
            NotifyMode: AutomationNotifyMode.None,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            LastRunAt: null,
            NextRunAt: null,
            LastRunStatus: null,
            LastError: null);

    private sealed class StubWorkspaceService : IWorkspaceService
    {
        public StubWorkspaceService(string rootPath) => RootPath = rootPath;

        public string RootPath { get; }

        public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceSnapshot(RootPath, KodaClawWorkspaceLayout.CurrentWorkspaceVersion, true, false, null, null));

        public Task<WorkspaceSnapshot> EnsureInitializedAsync(CancellationToken cancellationToken = default)
            => GetSnapshotAsync(cancellationToken);

        public Task<WorkspaceAppConfig> LoadAppConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceAppConfig());

        public Task SaveAppConfigAsync(WorkspaceAppConfig appConfig, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public string GetSessionDirectory(string sessionId)
            => Path.Combine(RootPath, KodaClawWorkspaceLayout.SessionsDirectory, sessionId);
        public IReadOnlyList<string> GetSkillsPaths() => [];

        public Task<WorkspaceMcpConfig> ReadMcpConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceMcpConfig());

        public Task SaveMcpConfigAsync(WorkspaceMcpConfig config, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<GatewayConfig> ReadGatewayConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GatewayConfig());

        public Task SaveGatewayConfigAsync(GatewayConfig config, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCommitWorkspaceAsync(string message, CancellationToken cancellationToken = default) => Task.FromResult(false);

    }
}
