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

public sealed class WorkspaceProtocolUpdateToolTests : IDisposable
{
    private readonly string _rootPath;
    private readonly WorkspaceProtocolUpdateTool _tool;

    public WorkspaceProtocolUpdateToolTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-protocol-tests", Guid.NewGuid().ToString("N"));
        _tool = new WorkspaceProtocolUpdateTool(new StubWorkspaceService(_rootPath));
    }

    // ── Tool metadata ────────────────────────────────────────────────────────

    [Fact]
    public void Tool_name_is_workspace_protocol_update()
        => _tool.Name.Should().Be("workspace_protocol_update");

    [Fact]
    public void Tool_does_not_require_approval()
        => _tool.Attributes.RequiresApproval.Should().BeFalse();

    [Fact]
    public void Tool_is_not_readonly()
        => _tool.Attributes.ReadOnly.Should().BeFalse();

    // ── ApplySectionPatch: section = null ────────────────────────────────────

    [Fact]
    public void Patch_no_section_replaces_body_after_title()
    {
        var original = "# Koda Identity\n\n- Name: Koda\n- Role: assistant\n";
        var result = WorkspaceProtocolUpdateTool.ApplySectionPatch(original, null, "- Name: Voda\n- Role: collaborator");

        result.Should().StartWith("# Koda Identity\n");
        result.Should().Contain("- Name: Voda");
        result.Should().NotContain("- Name: Koda");
    }

    [Fact]
    public void Patch_no_section_preserves_title_line()
    {
        var original = "# My Title\n\nOld content\n";
        var result = WorkspaceProtocolUpdateTool.ApplySectionPatch(original, null, "New content");

        result.Should().StartWith("# My Title\n");
        result.Should().Contain("New content");
        result.Should().NotContain("Old content");
    }

    // ── ApplySectionPatch: existing section ──────────────────────────────────

    [Fact]
    public void Patch_existing_section_replaces_only_that_section()
    {
        var original = """
# User Profile

## Working Style
- verbose

## Communication
- formal

""";
        var result = WorkspaceProtocolUpdateTool.ApplySectionPatch(original, "Working Style", "- terse and direct");

        result.Should().Contain("## Working Style\n- terse and direct");
        result.Should().NotContain("- verbose");
        result.Should().Contain("## Communication");
        result.Should().Contain("- formal");
    }

    [Fact]
    public void Patch_existing_section_is_case_insensitive()
    {
        var original = "# Soul\n\n## Principles\n- old rule\n";
        var result = WorkspaceProtocolUpdateTool.ApplySectionPatch(original, "principles", "- new rule");

        result.Should().Contain("- new rule");
        result.Should().NotContain("- old rule");
    }

    [Fact]
    public void Patch_last_section_replaces_correctly()
    {
        var original = "# Memory\n\n## Facts\n- fact one\n## Recent\n- recent one\n";
        var result = WorkspaceProtocolUpdateTool.ApplySectionPatch(original, "Recent", "- updated recent");

        result.Should().Contain("## Recent\n- updated recent");
        result.Should().NotContain("- recent one");
        result.Should().Contain("## Facts");
        result.Should().Contain("- fact one");
    }

    // ── ApplySectionPatch: missing section ───────────────────────────────────

    [Fact]
    public void Patch_missing_section_appends_at_end()
    {
        var original = "# User Profile\n\n- existing content\n";
        var result = WorkspaceProtocolUpdateTool.ApplySectionPatch(original, "New Section", "- new item");

        result.Should().Contain("- existing content");
        result.Should().Contain("## New Section\n- new item");
    }

    // ── Tool execution ────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_writes_file_to_workspace_directory()
    {
        var wsDir = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory);
        Directory.CreateDirectory(wsDir);
        await File.WriteAllTextAsync(
            Path.Combine(wsDir, KodaClawWorkspaceLayout.UserFile),
            "# User Profile\n\n- Name: Old\n");

        var result = await ExecuteAsync(new WorkspaceProtocolUpdateArgs
        {
            Target = "user",
            Content = "- Name: Van Zheng",
        });

        result.Success.Should().BeTrue();
        var contents = await File.ReadAllTextAsync(Path.Combine(wsDir, KodaClawWorkspaceLayout.UserFile));
        contents.Should().Contain("Van Zheng");
        contents.Should().NotContain("- Name: Old");
    }

    [Fact]
    public async Task Execute_returns_error_for_unknown_target()
    {
        var result = await ExecuteAsync(new WorkspaceProtocolUpdateArgs
        {
            Target = "unknown_file",
            Content = "anything",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("unknown_file");
    }

    [Fact]
    public async Task Execute_heartbeat_target_writes_to_heartbeat_md()
    {
        var wsDir = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory);
        Directory.CreateDirectory(wsDir);
        await File.WriteAllTextAsync(
            Path.Combine(wsDir, KodaClawWorkspaceLayout.HeartbeatFile),
            "# Heartbeat Automations\n\n");

        var result = await ExecuteAsync(new WorkspaceProtocolUpdateArgs
        {
            Target = "heartbeat",
            Section = "Daily Morning Digest",
            Content = "- schedule: daily 09:00\n- prompt: Review inbox\n- enabled: true",
        });

        result.Success.Should().BeTrue();
        var contents = await File.ReadAllTextAsync(
            Path.Combine(wsDir, KodaClawWorkspaceLayout.HeartbeatFile));
        contents.Should().Contain("## Daily Morning Digest");
        contents.Should().Contain("- schedule: daily 09:00");
        contents.Should().Contain("- enabled: true");
    }

    [Fact]
    public async Task Execute_heartbeat_target_creates_file_from_default_when_missing()
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory));

        var result = await ExecuteAsync(new WorkspaceProtocolUpdateArgs
        {
            Target = "heartbeat",
            Section = "Hourly Check",
            Content = "- schedule: hourly 1h\n- prompt: Check tasks\n- enabled: false",
        });

        result.Success.Should().BeTrue();
        var path = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.HeartbeatFile);
        File.Exists(path).Should().BeTrue();
        var contents = await File.ReadAllTextAsync(path);
        contents.Should().Contain("## Hourly Check");
    }

    [Fact]
    public async Task Execute_creates_file_from_default_when_missing()
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory));

        var result = await ExecuteAsync(new WorkspaceProtocolUpdateArgs
        {
            Target = "identity",
            Section = "Name",
            Content = "- Name: Koda",
        });

        result.Success.Should().BeTrue();
        var path = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.IdentityFile);
        File.Exists(path).Should().BeTrue();
        var contents = await File.ReadAllTextAsync(path);
        contents.Should().Contain("- Name: Koda");
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
