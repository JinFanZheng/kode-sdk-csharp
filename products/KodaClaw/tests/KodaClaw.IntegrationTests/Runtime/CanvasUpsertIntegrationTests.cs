using System.IO;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Canvas;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Tools;
using KodaClaw.Storage.Json.Repositories;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.IntegrationTests.Runtime;

public sealed class CanvasUpsertIntegrationTests : IDisposable
{
    private readonly string _rootPath;
    private readonly WorkspaceService _workspace;
    private readonly ICanvasArtifactRepository _canvasRepo;
    private readonly CanvasUpsertTool _tool;

    public CanvasUpsertIntegrationTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-canvas-integration", Guid.NewGuid().ToString("N"));
        _workspace = new WorkspaceService(new KodaClawWorkspaceOptions { RootPath = _rootPath });
        _canvasRepo = new JsonCanvasArtifactRepository(_rootPath);
        _tool = new CanvasUpsertTool(_workspace, _canvasRepo);
    }

    [Fact]
    public async Task Upsert_writes_file_and_registers_artifact()
    {
        await _workspace.EnsureInitializedAsync();

        var result = await ExecuteAsync(new CanvasUpsertArgs
        {
            Id = "integration-report",
            Title = "Integration Test Report",
            Kind = "report",
            Content = "# Integration Test\n\n- All tests passed.",
            ContentType = "markdown",
        });

        result.Success.Should().BeTrue();

        // File exists on disk
        var filePath = Path.Combine(
            _rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory,
            "canvas", "integration-report", "index.md");
        File.Exists(filePath).Should().BeTrue();
        var contents = await File.ReadAllTextAsync(filePath);
        contents.Should().Contain("# Integration Test");

        // Artifact registered in SQLite
        var artifact = await _canvasRepo.GetByIdAsync("integration-report");
        artifact.Should().NotBeNull();
        artifact!.Title.Should().Be("Integration Test Report");
        artifact.Source.Should().Be("agent");
        artifact.Kind.Should().Be(CanvasArtifactKind.Report);
        artifact.EntryPath.Should().Be("workspace/canvas/integration-report/index.md");
    }

    [Fact]
    public async Task Second_upsert_with_same_id_preserves_created_at()
    {
        await _workspace.EnsureInitializedAsync();

        await ExecuteAsync(new CanvasUpsertArgs
        {
            Id = "update-test",
            Title = "Original",
            Kind = "report",
            Content = "v1 content",
        });

        var firstArtifact = await _canvasRepo.GetByIdAsync("update-test");
        var originalCreatedAt = firstArtifact!.CreatedAt;

        await Task.Delay(50); // ensure time difference

        await ExecuteAsync(new CanvasUpsertArgs
        {
            Id = "update-test",
            Title = "Updated",
            Kind = "report",
            Content = "v2 content",
        });

        var updated = await _canvasRepo.GetByIdAsync("update-test");
        updated!.Title.Should().Be("Updated");
        updated.CreatedAt.Should().Be(originalCreatedAt);
        updated.UpdatedAt.Should().BeAfter(originalCreatedAt);
    }

    [Fact]
    public async Task Upsert_html_artifact_uses_html_extension()
    {
        await _workspace.EnsureInitializedAsync();

        var result = await ExecuteAsync(new CanvasUpsertArgs
        {
            Id = "html-test",
            Title = "HTML Dashboard",
            Kind = "dashboard",
            Content = "<html><body><h1>Dashboard</h1></body></html>",
            ContentType = "html",
        });

        result.Success.Should().BeTrue();
        var filePath = Path.Combine(
            _rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory,
            "canvas", "html-test", "index.html");
        File.Exists(filePath).Should().BeTrue();

        var artifact = await _canvasRepo.GetByIdAsync("html-test");
        artifact!.EntryPath.Should().Be("workspace/canvas/html-test/index.html");
    }

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
}
