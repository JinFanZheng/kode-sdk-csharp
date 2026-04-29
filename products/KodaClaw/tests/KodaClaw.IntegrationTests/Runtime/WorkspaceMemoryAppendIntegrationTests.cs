using System.IO;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Tools;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.IntegrationTests.Runtime;

public sealed class WorkspaceMemoryAppendIntegrationTests : IDisposable
{
    private readonly string _rootPath;
    private readonly WorkspaceService _workspace;
    private readonly WorkspaceMemoryAppendTool _tool;

    public WorkspaceMemoryAppendIntegrationTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-mem-append-tests", Guid.NewGuid().ToString("N"));
        _workspace = new WorkspaceService(new KodaClawWorkspaceOptions { RootPath = _rootPath });
        _tool = new WorkspaceMemoryAppendTool(_workspace);
    }

    [Fact]
    public async Task Appends_entry_to_todays_memory_file_after_workspace_initialized()
    {
        await _workspace.EnsureInitializedAsync();

        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var expectedPath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, "memory", $"{today}.md");

        var result = await ExecuteAsync(new WorkspaceMemoryAppendArgs
        {
            Content = "- Fact: workspace integration test ran",
        });

        result.Success.Should().BeTrue();
        File.Exists(expectedPath).Should().BeTrue();
        var contents = await File.ReadAllTextAsync(expectedPath);
        contents.Should().Contain("workspace integration test ran");
    }

    [Fact]
    public async Task Entry_contains_timestamp_prefix()
    {
        await _workspace.EnsureInitializedAsync();

        await ExecuteAsync(new WorkspaceMemoryAppendArgs
        {
            Content = "- Fact: timestamp test",
        });

        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, "memory", $"{today}.md");
        var contents = await File.ReadAllTextAsync(filePath);

        // Entry has HTML comment timestamp + priority prefix: <!-- HH:mm | priority -->
        contents.Should().MatchRegex(@"<!-- \d{2}:\d{2} \| \w+ -->");
    }

    [Fact]
    public async Task Successive_appends_do_not_overwrite_earlier_entries()
    {
        await _workspace.EnsureInitializedAsync();

        await ExecuteAsync(new WorkspaceMemoryAppendArgs { Content = "- Fact: alpha" });
        await ExecuteAsync(new WorkspaceMemoryAppendArgs { Content = "- Fact: beta" });
        await ExecuteAsync(new WorkspaceMemoryAppendArgs { Content = "- Fact: gamma" });

        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, "memory", $"{today}.md");
        var contents = await File.ReadAllTextAsync(filePath);

        contents.Should().Contain("alpha");
        contents.Should().Contain("beta");
        contents.Should().Contain("gamma");
    }

    [Fact]
    public async Task Can_write_to_a_past_date()
    {
        await _workspace.EnsureInitializedAsync();

        var pastDate = "2026-03-01";
        var expectedPath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, "memory", $"{pastDate}.md");

        var result = await ExecuteAsync(new WorkspaceMemoryAppendArgs
        {
            Content = "- Fact: past date entry",
            Date = pastDate,
        });

        result.Success.Should().BeTrue();
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public async Task Tool_result_reports_correct_file_path()
    {
        await _workspace.EnsureInitializedAsync();

        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var result = await ExecuteAsync(new WorkspaceMemoryAppendArgs { Content = "- Fact: path check" });

        result.Success.Should().BeTrue();
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        json.Should().Contain(today);
        json.Should().Contain(_rootPath.Replace("\\", "\\\\").Replace("/", "/"));
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
            CallId = Guid.NewGuid().ToString("N"),
            Sandbox = sandbox.Object,
        };
        return _tool.ExecuteAsync((object)args, context, CancellationToken.None);
    }
}
