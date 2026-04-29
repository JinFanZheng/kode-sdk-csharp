using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Workspace;
using KodaClaw.McpHub;
using Kode.Agent.Mcp;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.ContractTests.McpHub;

/// <summary>
/// Contract tests for McpHubService injection behavior.
/// These tests validate the service's filtering and isolation logic
/// without establishing real MCP connections.
/// </summary>
public sealed class McpHubInjectionContractTests
{
    [Fact]
    public async Task InjectToolsAsync_returns_empty_when_config_has_no_servers()
    {
        var workspaceService = CreateWorkspaceServiceWithConfig(new WorkspaceMcpConfig());
        var toolRegistry = new Mock<IToolRegistry>().Object;
        var service = new McpHubService(workspaceService, mcpClientManager: new McpClientManager());

        var result = await service.InjectToolsAsync("session-001", SessionKind.Main, toolRegistry);

        result.Should().Be(McpHubInjectionResult.Empty);
    }

    [Fact]
    public async Task InjectToolsAsync_returns_empty_when_mcpClientManager_is_null()
    {
        var config = new WorkspaceMcpConfig
        {
            McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
            {
                ["server-a"] = new WorkspaceMcpServerEntry { Command = "npx" }
            }
        };
        var workspaceService = CreateWorkspaceServiceWithConfig(config);
        var toolRegistry = new Mock<IToolRegistry>().Object;
        var service = new McpHubService(workspaceService, mcpClientManager: null);

        var result = await service.InjectToolsAsync("session-001", SessionKind.Main, toolRegistry);

        result.Should().Be(McpHubInjectionResult.Empty);
    }

    [Fact]
    public async Task InjectToolsAsync_skips_disabled_entries_and_records_zero_for_them()
    {
        // Only enabled=false entries — with a real McpClientManager, the service
        // should skip them all and return zero tools injected (not attempt to connect).
        var config = new WorkspaceMcpConfig
        {
            McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
            {
                ["disabled-server"] = new WorkspaceMcpServerEntry
                {
                    Command = "npx",
                    Args = ["-y", "some-server@latest"],
                    Enabled = false,
                }
            }
        };
        var workspaceService = CreateWorkspaceServiceWithConfig(config);
        var toolRegistry = new Mock<IToolRegistry>().Object;
        // Use a real McpClientManager — with enabled=false, it should never be called
        var service = new McpHubService(workspaceService, mcpClientManager: new McpClientManager());

        var result = await service.InjectToolsAsync("session-001", SessionKind.Main, toolRegistry);

        result.ToolCount.Should().Be(0);
        result.FailedServerCount.Should().Be(0);
        result.InjectedToolNames.Should().BeEmpty();
    }

    [Fact]
    public async Task InjectToolsAsync_skips_entries_with_enabled_null_treated_as_enabled()
    {
        // enabled=null means "not specified" → treated as enabled=true (Claude Desktop compat)
        // With a real but unconnectable command, it should fail the server (not skip it)
        var config = new WorkspaceMcpConfig
        {
            McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
            {
                ["null-enabled-server"] = new WorkspaceMcpServerEntry
                {
                    Command = "nonexistent-command-that-will-fail",
                    Enabled = null,
                }
            }
        };
        var workspaceService = CreateWorkspaceServiceWithConfig(config);
        var toolRegistry = new Mock<IToolRegistry>().Object;
        var service = new McpHubService(workspaceService, mcpClientManager: new McpClientManager());

        var result = await service.InjectToolsAsync("session-001", SessionKind.Main, toolRegistry);

        // The server was attempted (not skipped), but failed — so failedServerCount=1
        result.ToolCount.Should().Be(0);
        result.FailedServerCount.Should().Be(1);
        result.FailedServers.Should().ContainSingle("null-enabled-server");
    }

    [Fact]
    public async Task InjectToolsAsync_isolates_single_server_failure_from_others()
    {
        // Two servers: one that will fail (bad command), one disabled (skipped cleanly)
        // The disabled one should not increase FailedServerCount
        var config = new WorkspaceMcpConfig
        {
            McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
            {
                ["bad-server"] = new WorkspaceMcpServerEntry
                {
                    Command = "nonexistent-command-xyz-12345",
                },
                ["disabled-server"] = new WorkspaceMcpServerEntry
                {
                    Command = "npx",
                    Enabled = false,
                }
            }
        };
        var workspaceService = CreateWorkspaceServiceWithConfig(config);
        var toolRegistry = new Mock<IToolRegistry>().Object;
        var service = new McpHubService(workspaceService, mcpClientManager: new McpClientManager());

        var result = await service.InjectToolsAsync("session-001", SessionKind.Main, toolRegistry);

        result.ToolCount.Should().Be(0);
        result.FailedServerCount.Should().Be(1, "only bad-server fails; disabled-server is skipped, not failed");
        result.FailedServers.Should().ContainSingle("bad-server");
    }

    [Fact]
    public async Task McpHubInjectionResult_empty_singleton_has_zero_counts()
    {
        McpHubInjectionResult.Empty.ServerCount.Should().Be(0);
        McpHubInjectionResult.Empty.ToolCount.Should().Be(0);
        McpHubInjectionResult.Empty.FailedServerCount.Should().Be(0);
        McpHubInjectionResult.Empty.FailedServers.Should().BeEmpty();
        McpHubInjectionResult.Empty.InjectedToolNames.Should().BeEmpty();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task InjectToolsAsync_filters_by_session_kind_dm()
    {
        // Server scoped to "main" only — DM session should skip it
        var config = new WorkspaceMcpConfig
        {
            McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
            {
                ["main-only-server"] = new WorkspaceMcpServerEntry
                {
                    Command = "npx",
                    SessionScopes = ["main"],
                }
            }
        };
        var workspaceService = CreateWorkspaceServiceWithConfig(config);
        var toolRegistry = new Mock<IToolRegistry>().Object;
        var service = new McpHubService(workspaceService, mcpClientManager: new McpClientManager());

        var result = await service.InjectToolsAsync("session-dm", SessionKind.ChannelDirectMessage, toolRegistry);

        // Should be skipped (scope filtered), not failed
        result.ToolCount.Should().Be(0);
        result.FailedServerCount.Should().Be(0, "scope-filtered servers are not counted as failures");
    }

    [Fact]
    public async Task InjectToolsAsync_filters_by_session_kind_automation()
    {
        // Server scoped to dm+main — automation should skip it
        var config = new WorkspaceMcpConfig
        {
            McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
            {
                ["chat-only-server"] = new WorkspaceMcpServerEntry
                {
                    Command = "npx",
                    SessionScopes = ["dm", "main"],
                }
            }
        };
        var workspaceService = CreateWorkspaceServiceWithConfig(config);
        var toolRegistry = new Mock<IToolRegistry>().Object;
        var service = new McpHubService(workspaceService, mcpClientManager: new McpClientManager());

        var result = await service.InjectToolsAsync("session-auto", SessionKind.Automation, toolRegistry);

        result.ToolCount.Should().Be(0);
        result.FailedServerCount.Should().Be(0, "scope-filtered servers are not counted as failures");
    }

    [Fact]
    public async Task InjectToolsAsync_allows_when_scope_contains_all()
    {
        // Server with sessionScopes: ["all"] — all sessions should attempt to connect
        // With an unconnectable command it will fail (not be skipped)
        var config = new WorkspaceMcpConfig
        {
            McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
            {
                ["all-scopes-server"] = new WorkspaceMcpServerEntry
                {
                    Command = "nonexistent-command-will-fail",
                    SessionScopes = ["all"],
                }
            }
        };
        var workspaceService = CreateWorkspaceServiceWithConfig(config);
        var toolRegistry = new Mock<IToolRegistry>().Object;
        var service = new McpHubService(workspaceService, mcpClientManager: new McpClientManager());

        var result = await service.InjectToolsAsync("session-dm", SessionKind.ChannelDirectMessage, toolRegistry);

        // Server was attempted (not scope-filtered), so it fails
        result.FailedServerCount.Should().Be(1, "server with 'all' scope should be attempted for all sessions");
    }

    [Fact]
    public async Task InjectToolsAsync_allows_when_sessionScopes_is_null()
    {
        // sessionScopes = null → all session types (backward compatible)
        var config = new WorkspaceMcpConfig
        {
            McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
            {
                ["legacy-server"] = new WorkspaceMcpServerEntry
                {
                    Command = "nonexistent-command-will-fail",
                    SessionScopes = null,
                }
            }
        };
        var workspaceService = CreateWorkspaceServiceWithConfig(config);
        var toolRegistry = new Mock<IToolRegistry>().Object;
        var service = new McpHubService(workspaceService, mcpClientManager: new McpClientManager());

        var result = await service.InjectToolsAsync("session-auto", SessionKind.Automation, toolRegistry);

        result.FailedServerCount.Should().Be(1, "null sessionScopes means all sessions — server should be attempted");
    }

    [Fact]
    public async Task InjectToolsAsync_allows_when_sessionScopes_is_empty_array()
    {
        // sessionScopes = [] → treated as all (empty array is not a valid restriction)
        var config = new WorkspaceMcpConfig
        {
            McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
            {
                ["empty-scopes-server"] = new WorkspaceMcpServerEntry
                {
                    Command = "nonexistent-command-will-fail",
                    SessionScopes = [],
                }
            }
        };
        var workspaceService = CreateWorkspaceServiceWithConfig(config);
        var toolRegistry = new Mock<IToolRegistry>().Object;
        var service = new McpHubService(workspaceService, mcpClientManager: new McpClientManager());

        var result = await service.InjectToolsAsync("session-group", SessionKind.ChannelGroup, toolRegistry);

        result.FailedServerCount.Should().Be(1, "empty sessionScopes is treated as 'all' — server should be attempted");
    }

    private static IWorkspaceService CreateWorkspaceServiceWithConfig(WorkspaceMcpConfig config)
    {
        var mock = new Mock<IWorkspaceService>();
        mock.Setup(s => s.ReadMcpConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        return mock.Object;
    }
}
