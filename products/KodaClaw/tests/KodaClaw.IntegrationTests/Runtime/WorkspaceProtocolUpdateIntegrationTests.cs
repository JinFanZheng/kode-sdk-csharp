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

public sealed class WorkspaceProtocolUpdateIntegrationTests : IDisposable
{
    private readonly string _rootPath;
    private readonly WorkspaceService _workspace;
    private readonly WorkspaceProtocolUpdateTool _tool;

    public WorkspaceProtocolUpdateIntegrationTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-protocol-int-tests", Guid.NewGuid().ToString("N"));
        _workspace = new WorkspaceService(new KodaClawWorkspaceOptions { RootPath = _rootPath });
        _tool = new WorkspaceProtocolUpdateTool(_workspace);
    }

    [Fact]
    public async Task Updates_user_profile_without_section()
    {
        await _workspace.EnsureInitializedAsync();

        var result = await ExecuteAsync(new WorkspaceProtocolUpdateArgs
        {
            Target = "user",
            Content = "- Name: Van Zheng\n- Preferred working style: terse\n- Boundaries: no marketing content",
        });

        result.Success.Should().BeTrue();
        var path = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.UserFile);
        var contents = await File.ReadAllTextAsync(path);
        contents.Should().Contain("Van Zheng");
        contents.Should().Contain("terse");
        contents.Should().StartWith("# User Profile");
    }

    [Fact]
    public async Task Updates_specific_section_preserves_other_sections()
    {
        await _workspace.EnsureInitializedAsync();

        // Write initial file with two sections.
        var wsDir = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(wsDir, KodaClawWorkspaceLayout.UserFile),
            "# User Profile\n\n## Working Style\n- verbose\n\n## Communication\n- formal\n");

        var result = await ExecuteAsync(new WorkspaceProtocolUpdateArgs
        {
            Target = "user",
            Section = "Working Style",
            Content = "- terse and direct",
        });

        result.Success.Should().BeTrue();
        var contents = await File.ReadAllTextAsync(Path.Combine(wsDir, KodaClawWorkspaceLayout.UserFile));
        contents.Should().Contain("- terse and direct");
        contents.Should().NotContain("- verbose");
        contents.Should().Contain("## Communication");
        contents.Should().Contain("- formal");
    }

    [Fact]
    public async Task Successive_updates_to_same_section_reflect_latest_value()
    {
        await _workspace.EnsureInitializedAsync();

        await ExecuteAsync(new WorkspaceProtocolUpdateArgs
        {
            Target = "identity",
            Section = "Name",
            Content = "- Name: Koda v1",
        });

        await ExecuteAsync(new WorkspaceProtocolUpdateArgs
        {
            Target = "identity",
            Section = "Name",
            Content = "- Name: Koda v2",
        });

        var path = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.IdentityFile);
        var contents = await File.ReadAllTextAsync(path);
        contents.Should().Contain("Koda v2");
        contents.Should().NotContain("Koda v1");
    }

    [Fact]
    public async Task Appends_new_section_when_it_does_not_exist()
    {
        await _workspace.EnsureInitializedAsync();

        var result = await ExecuteAsync(new WorkspaceProtocolUpdateArgs
        {
            Target = "soul",
            Section = "Custom Rules",
            Content = "- Always respond in Chinese",
        });

        result.Success.Should().BeTrue();
        var path = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.SoulFile);
        var contents = await File.ReadAllTextAsync(path);
        contents.Should().Contain("## Custom Rules");
        contents.Should().Contain("- Always respond in Chinese");
    }

    [Fact]
    public async Task Can_update_memory_md_for_nightly_consolidation_use_case()
    {
        await _workspace.EnsureInitializedAsync();

        var consolidated = "- Van prefers terse replies.\n- Van works in CST timezone.\n- Van is building KodaClaw.";

        var result = await ExecuteAsync(new WorkspaceProtocolUpdateArgs
        {
            Target = "memory",
            Content = consolidated,
        });

        result.Success.Should().BeTrue();
        var path = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.MemoryFile);
        var contents = await File.ReadAllTextAsync(path);
        contents.Should().Contain("terse replies");
        contents.Should().Contain("KodaClaw");
        contents.Should().StartWith("# Long-Term Memory");
    }

    [Fact]
    public async Task Result_contains_target_and_path()
    {
        await _workspace.EnsureInitializedAsync();

        var result = await ExecuteAsync(new WorkspaceProtocolUpdateArgs
        {
            Target = "agents",
            Content = "- Be helpful.",
        });

        result.Success.Should().BeTrue();
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        json.Should().Contain("agents");
        json.Should().Contain(_rootPath.Replace("\\", "\\\\"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private Task<ToolResult> ExecuteAsync(WorkspaceProtocolUpdateArgs args)
    {
        var context = new ToolContext
        {
            AgentId = "test-agent",
            CallId = Guid.NewGuid().ToString("N"),
            Sandbox = new Mock<ISandbox>().Object,
        };
        return _tool.ExecuteAsync((object)args, context, CancellationToken.None);
    }
}
