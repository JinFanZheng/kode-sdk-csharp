using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Tools;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class TopicsWorkspaceReadTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly Mock<IWorkspaceService> _workspaceMock;
    private readonly WorkspaceReadTool _tool;

    public TopicsWorkspaceReadTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"topics-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);

        _workspaceMock = new Mock<IWorkspaceService>();
        _workspaceMock.SetupGet(w => w.RootPath).Returns(_workspaceRoot);

        _tool = new WorkspaceReadTool(_workspaceMock.Object);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Topics_ListAll_ReturnsTopicNames()
    {
        var topicsDir = Path.Combine(_workspaceRoot, KodaClawWorkspaceLayout.MemoryTopicsDirectory);
        Directory.CreateDirectory(topicsDir);
        await File.WriteAllTextAsync(Path.Combine(topicsDir, "frontend-architecture.md"), "# Frontend Architecture");
        await File.WriteAllTextAsync(Path.Combine(topicsDir, "testing-strategy.md"), "# Testing Strategy");

        var result = await ExecuteAsync(new WorkspaceReadArgs { Target = "topics" });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("target").GetString().Should().Be("topics");
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
        var topics = doc.RootElement.GetProperty("topics");
        topics.GetArrayLength().Should().Be(2);
        var topicNames = topics.EnumerateArray().Select(e => e.GetString()).ToList();
        topicNames.Should().Contain("frontend-architecture");
        topicNames.Should().Contain("testing-strategy");
    }

    [Fact]
    public async Task Topics_ReadSpecific_ReturnsContent()
    {
        var topicsDir = Path.Combine(_workspaceRoot, KodaClawWorkspaceLayout.MemoryTopicsDirectory);
        Directory.CreateDirectory(topicsDir);
        var expectedContent = "# Frontend Architecture\n\nReact + Vite setup.";
        await File.WriteAllTextAsync(Path.Combine(topicsDir, "frontend-architecture.md"), expectedContent);

        var result = await ExecuteAsync(new WorkspaceReadArgs { Target = "topics", Path = "frontend-architecture" });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("exists").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("content").GetString().Should().Be(expectedContent);
        doc.RootElement.GetProperty("path").GetString().Should().Be("frontend-architecture");
    }

    [Fact]
    public async Task Topics_EmptyDirectory_ReturnsEmptyList()
    {
        var topicsDir = Path.Combine(_workspaceRoot, KodaClawWorkspaceLayout.MemoryTopicsDirectory);
        Directory.CreateDirectory(topicsDir);

        var result = await ExecuteAsync(new WorkspaceReadArgs { Target = "topics" });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("topics").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Topics_NonexistentTopic_ReturnsExistsFalse()
    {
        var topicsDir = Path.Combine(_workspaceRoot, KodaClawWorkspaceLayout.MemoryTopicsDirectory);
        Directory.CreateDirectory(topicsDir);

        var result = await ExecuteAsync(new WorkspaceReadArgs { Target = "topics", Path = "nonexistent-topic" });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("exists").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("content").ValueKind.Should().Be(JsonValueKind.Null);
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
        return _tool.ExecuteAsync(args, context, CancellationToken.None);
    }
}
