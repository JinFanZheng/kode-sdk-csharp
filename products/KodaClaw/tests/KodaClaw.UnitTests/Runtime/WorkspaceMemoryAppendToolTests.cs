using System.IO;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Tools;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class WorkspaceMemoryAppendToolTests : IDisposable
{
    private readonly string _rootPath;
    private readonly WorkspaceMemoryAppendTool _tool;

    public WorkspaceMemoryAppendToolTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-tool-tests", Guid.NewGuid().ToString("N"));
        var workspaceService = new StubWorkspaceService(_rootPath);
        _tool = new WorkspaceMemoryAppendTool(workspaceService);
    }

    [Fact]
    public void Tool_name_is_workspace_memory_append()
    {
        _tool.Name.Should().Be("workspace_memory_append");
    }

    [Fact]
    public void Tool_does_not_require_approval()
    {
        _tool.Attributes.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void Tool_is_not_readonly()
    {
        _tool.Attributes.ReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task Appends_content_to_daily_memory_file()
    {
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var expectedPath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, "memory", $"{today}.md");

        var result = await ExecuteAsync(new WorkspaceMemoryAppendArgs
        {
            Content = "- Fact: tests are important",
        });

        result.Success.Should().BeTrue();
        File.Exists(expectedPath).Should().BeTrue();
        var contents = await File.ReadAllTextAsync(expectedPath);
        contents.Should().Contain("tests are important");
    }

    [Fact]
    public async Task Multiple_appends_accumulate_in_same_file()
    {
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, "memory", $"{today}.md");

        await ExecuteAsync(new WorkspaceMemoryAppendArgs { Content = "- Entry: first memory" });
        await ExecuteAsync(new WorkspaceMemoryAppendArgs { Content = "- Entry: second memory" });

        var contents = await File.ReadAllTextAsync(filePath);
        contents.Should().Contain("first memory");
        contents.Should().Contain("second memory");
    }

    [Fact]
    public async Task Uses_provided_date_parameter()
    {
        var customDate = "2026-01-15";
        var expectedPath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, "memory", $"{customDate}.md");

        var result = await ExecuteAsync(new WorkspaceMemoryAppendArgs
        {
            Content = "- Fact: custom date entry",
            Date = customDate,
        });

        result.Success.Should().BeTrue();
        File.Exists(expectedPath).Should().BeTrue();
        var contents = await File.ReadAllTextAsync(expectedPath);
        contents.Should().Contain("custom date entry");
    }

    [Fact]
    public async Task Creates_memory_directory_if_missing()
    {
        var memoryDir = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, "memory");
        Directory.Exists(memoryDir).Should().BeFalse();

        var result = await ExecuteAsync(new WorkspaceMemoryAppendArgs
        {
            Content = "- Fact: directory creation test",
        });

        result.Success.Should().BeTrue();
        Directory.Exists(memoryDir).Should().BeTrue();
    }

    [Fact]
    public async Task Result_contains_date_and_path()
    {
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");

        var result = await ExecuteAsync(new WorkspaceMemoryAppendArgs
        {
            Content = "- Fact: result check",
        });

        result.Success.Should().BeTrue();
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        json.Should().Contain(today);
        json.Should().Contain("memory");
    }

    [Fact]
    public async Task Writes_default_standard_priority_when_omitted()
    {
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, "memory", $"{today}.md");

        await ExecuteAsync(new WorkspaceMemoryAppendArgs { Content = "- Fact: no priority" });

        var contents = await File.ReadAllTextAsync(filePath);
        contents.Should().MatchRegex(@"<!-- \d{2}:\d{2} \| standard -->");
    }

    [Fact]
    public async Task Writes_specified_priority_in_entry()
    {
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, "memory", $"{today}.md");

        await ExecuteAsync(new WorkspaceMemoryAppendArgs { Content = "- Fact: lasting entry", Priority = "lasting" });

        var contents = await File.ReadAllTextAsync(filePath);
        contents.Should().MatchRegex(@"<!-- \d{2}:\d{2} \| lasting -->");
    }

    [Fact]
    public async Task Invalid_priority_falls_back_to_standard()
    {
        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, "memory", $"{today}.md");

        await ExecuteAsync(new WorkspaceMemoryAppendArgs { Content = "- Fact: bad priority", Priority = "invalid-value" });

        var contents = await File.ReadAllTextAsync(filePath);
        contents.Should().MatchRegex(@"<!-- \d{2}:\d{2} \| standard -->");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private Task<ToolResult> ExecuteAsync(WorkspaceMemoryAppendArgs args)
    {
        var sandbox = new Mock<ISandbox>();
        var context = new ToolContext
        {
            AgentId = "test-agent",
            CallId = "test-call",
            Sandbox = sandbox.Object,
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
