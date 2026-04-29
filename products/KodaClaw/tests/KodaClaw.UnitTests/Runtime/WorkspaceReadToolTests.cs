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

public sealed class WorkspaceReadToolTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly Mock<IWorkspaceService> _workspaceMock;
    private readonly WorkspaceReadTool _tool;

    public WorkspaceReadToolTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"wsr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);

        _workspaceMock = new Mock<IWorkspaceService>();
        _workspaceMock.SetupGet(w => w.RootPath).Returns(_workspaceRoot);

        _tool = new WorkspaceReadTool(_workspaceMock.Object);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* ignore */ }
    }

    // ── Metadata ──────────────────────────────────────────────────────────────

    [Fact]
    public void Tool_name_is_workspace_read()
        => _tool.Name.Should().Be("workspace_read");

    [Fact]
    public void Tool_does_not_require_approval()
        => _tool.Attributes.RequiresApproval.Should().BeFalse();

    [Fact]
    public void Tool_is_readonly()
        => _tool.Attributes.ReadOnly.Should().BeTrue();

    // ── Valid targets read files ───────────────────────────────────────────────

    [Theory]
    [InlineData("identity", "IDENTITY.md")]
    [InlineData("soul", "SOUL.md")]
    [InlineData("user", "USER.md")]
    [InlineData("memory", "MEMORY.md")]
    [InlineData("agents", "AGENTS.md")]
    [InlineData("heartbeat", "HEARTBEAT.md")]
    public async Task Execute_reads_existing_protocol_file(string target, string fileName)
    {
        var workspaceDir = Path.Combine(_workspaceRoot, "workspace");
        Directory.CreateDirectory(workspaceDir);
        var filePath = Path.Combine(workspaceDir, fileName);
        var expectedContent = $"# {target} content";
        await File.WriteAllTextAsync(filePath, expectedContent);

        var result = await ExecuteAsync(new WorkspaceReadArgs { Target = target });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("exists").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("content").GetString().Should().Be(expectedContent);
        doc.RootElement.GetProperty("target").GetString().Should().Be(target);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("memory")]
    [InlineData("heartbeat")]
    public async Task Execute_returns_exists_false_when_file_missing(string target)
    {
        // workspace dir exists but file does not
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, "workspace"));

        var result = await ExecuteAsync(new WorkspaceReadArgs { Target = target });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("exists").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("content").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── daily_memory ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_reads_daily_memory_file_for_today()
    {
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var memoryDir = Path.Combine(_workspaceRoot, "workspace", "memory");
        Directory.CreateDirectory(memoryDir);
        var filePath = Path.Combine(memoryDir, $"{today}.md");
        var expectedContent = "<!-- 09:00 -->\nRemember this important thing.";
        await File.WriteAllTextAsync(filePath, expectedContent);

        var result = await ExecuteAsync(new WorkspaceReadArgs { Target = "daily_memory" });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("exists").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("content").GetString().Should().Be(expectedContent);
        doc.RootElement.GetProperty("path").GetString().Should().Contain(today);
    }

    [Fact]
    public async Task Execute_returns_exists_false_for_missing_daily_memory()
    {
        // No memory directory at all
        var result = await ExecuteAsync(new WorkspaceReadArgs { Target = "daily_memory" });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("exists").GetBoolean().Should().BeFalse();
    }

    // ── Unknown target ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_fails_for_unknown_target()
    {
        var result = await ExecuteAsync(new WorkspaceReadArgs { Target = "nonexistent" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("nonexistent");
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
