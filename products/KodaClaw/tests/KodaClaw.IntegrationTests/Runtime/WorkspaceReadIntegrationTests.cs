using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Tools;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.IntegrationTests.Runtime;

public sealed class WorkspaceReadIntegrationTests : IDisposable
{
    private readonly string _rootPath;
    private readonly WorkspaceService _workspace;
    private readonly WorkspaceReadTool _tool;

    public WorkspaceReadIntegrationTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-wsread-int-tests", Guid.NewGuid().ToString("N"));
        _workspace = new WorkspaceService(new KodaClawWorkspaceOptions { RootPath = _rootPath });
        _tool = new WorkspaceReadTool(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootPath, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Reads_identity_file_after_workspace_initialization()
    {
        await _workspace.EnsureInitializedAsync();

        var result = await ExecuteAsync(new WorkspaceReadArgs { Target = "identity" });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("exists").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("content").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("target").GetString().Should().Be("identity");
    }

    [Fact]
    public async Task Returns_exists_false_for_missing_daily_memory_on_fresh_workspace()
    {
        await _workspace.EnsureInitializedAsync();

        var result = await ExecuteAsync(new WorkspaceReadArgs { Target = "daily_memory" });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("exists").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Reads_daily_memory_file_written_by_memory_append_tool()
    {
        await _workspace.EnsureInitializedAsync();

        // Write a memory entry using the append tool (simulating real usage).
        var appendTool = new WorkspaceMemoryAppendTool(_workspace);
        var appendCtx = new ToolContext
        {
            AgentId = "test-agent",
            CallId = "append-call",
            Sandbox = new Mock<ISandbox>().Object,
        };
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        await appendTool.ExecuteAsync(
            (object)new WorkspaceMemoryAppendArgs { Content = "Test memory entry for integration." },
            appendCtx,
            CancellationToken.None);

        // Now read it back.
        var result = await ExecuteAsync(new WorkspaceReadArgs { Target = "daily_memory" });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("exists").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("content").GetString().Should().Contain("Test memory entry for integration.");
        doc.RootElement.GetProperty("path").GetString().Should().Contain(today);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<ToolResult> ExecuteAsync(WorkspaceReadArgs args)
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
