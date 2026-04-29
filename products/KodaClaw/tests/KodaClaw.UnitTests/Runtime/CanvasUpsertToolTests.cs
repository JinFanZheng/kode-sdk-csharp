using System.IO;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Canvas;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Tools;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class CanvasUpsertToolTests : IDisposable
{
    private readonly string _rootPath;
    private readonly Mock<ICanvasArtifactRepository> _repoMock;
    private readonly CanvasUpsertTool _tool;

    public CanvasUpsertToolTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-canvas-tests", Guid.NewGuid().ToString("N"));
        _repoMock = new Mock<ICanvasArtifactRepository>();
        _repoMock
            .Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CanvasArtifact?)null);
        _tool = new CanvasUpsertTool(new StubWorkspaceService(_rootPath), _repoMock.Object);
    }

    // ── Metadata ──────────────────────────────────────────────────────────────

    [Fact]
    public void Tool_name_is_canvas_upsert()
        => _tool.Name.Should().Be("canvas_upsert");

    [Fact]
    public void Tool_does_not_require_approval()
        => _tool.Attributes.RequiresApproval.Should().BeFalse();

    [Fact]
    public void Tool_is_not_readonly()
        => _tool.Attributes.ReadOnly.Should().BeFalse();

    // ── Markdown content ──────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_writes_markdown_file_to_canvas_directory()
    {
        var result = await ExecuteAsync(new CanvasUpsertArgs
        {
            Id = "test-report-01",
            Title = "Weekly Report",
            Kind = "report",
            Content = "# Weekly Report\n\n- Item A\n- Item B",
            ContentType = "markdown",
        });

        result.Success.Should().BeTrue();
        var filePath = Path.Combine(
            _rootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            "canvas", "test-report-01", "index.md");
        File.Exists(filePath).Should().BeTrue();
        var contents = await File.ReadAllTextAsync(filePath);
        contents.Should().Contain("# Weekly Report");
    }

    // ── HTML content ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_writes_html_file_for_html_content_type()
    {
        var result = await ExecuteAsync(new CanvasUpsertArgs
        {
            Id = "test-html-01",
            Title = "Dashboard",
            Kind = "html",
            Content = "<h1>Dashboard</h1>",
            ContentType = "html",
        });

        result.Success.Should().BeTrue();
        var filePath = Path.Combine(
            _rootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            "canvas", "test-html-01", "index.html");
        File.Exists(filePath).Should().BeTrue();
    }

    // ── Auto-generated ID ─────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_generates_id_when_not_provided()
    {
        var result = await ExecuteAsync(new CanvasUpsertArgs
        {
            Title = "Auto ID Report",
            Kind = "report",
            Content = "Some content",
        });

        result.Success.Should().BeTrue();
        // id should be in the result
        result.Value.Should().NotBeNull();
    }

    // ── Kind validation ───────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_returns_error_for_invalid_kind()
    {
        var result = await ExecuteAsync(new CanvasUpsertArgs
        {
            Title = "Bad",
            Kind = "invalid_kind",
            Content = "x",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("invalid_kind");
    }

    // ── Repository upsert ─────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_calls_repository_upsert_with_agent_source()
    {
        CanvasArtifact? captured = null;
        _repoMock
            .Setup(r => r.UpsertAsync(It.IsAny<CanvasArtifact>(), It.IsAny<CancellationToken>()))
            .Callback<CanvasArtifact, CancellationToken>((a, _) => captured = a)
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new CanvasUpsertArgs
        {
            Id = "repo-test",
            Title = "Repo Test",
            Kind = "tasklist",
            Content = "- Task 1",
        });

        _repoMock.Verify(r => r.UpsertAsync(It.IsAny<CanvasArtifact>(), It.IsAny<CancellationToken>()), Times.Once);
        captured.Should().NotBeNull();
        captured!.Source.Should().Be("agent");
        captured.Id.Should().Be("repo-test");
        captured.Kind.Should().Be(CanvasArtifactKind.TaskList);
    }

    // ── EntryPath format ──────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_entry_path_is_workspace_relative()
    {
        CanvasArtifact? captured = null;
        _repoMock
            .Setup(r => r.UpsertAsync(It.IsAny<CanvasArtifact>(), It.IsAny<CancellationToken>()))
            .Callback<CanvasArtifact, CancellationToken>((a, _) => captured = a)
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new CanvasUpsertArgs
        {
            Id = "path-test",
            Title = "Path Test",
            Kind = "report",
            Content = "# Report",
        });

        captured!.EntryPath.Should().Be("workspace/canvas/path-test/index.md");
        captured.AssetDirectory.Should().Be("workspace/canvas/path-test");
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private Task<ToolResult> ExecuteAsync(CanvasUpsertArgs args)
    {
        var context = new ToolContext
        {
            AgentId = "test-agent",
            CallId = "test-call",
            Sandbox = new Mock<ISandbox>().Object,
        };
        return _tool.ExecuteAsync((object)args, context, CancellationToken.None);
    }

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
