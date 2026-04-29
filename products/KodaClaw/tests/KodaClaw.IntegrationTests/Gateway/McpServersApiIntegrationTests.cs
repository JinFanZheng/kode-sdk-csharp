using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class McpServersApiIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Get_mcp_servers_should_require_token()
    {
        using var workspace = new McpTempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path);
        await using var hosted = await StartMcpGatewayAsync(workspace.Path);

        var response = await hosted.Client.GetAsync("/api/mcp-servers");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_mcp_servers_returns_empty_config_when_no_file_exists()
    {
        using var workspace = new McpTempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path);
        await using var hosted = await StartMcpGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/mcp-servers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<WorkspaceMcpConfig>(JsonOptions);
        payload.Should().NotBeNull();
        payload!.McpServers.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_mcp_servers_saves_config_and_get_reads_it_back()
    {
        using var workspace = new McpTempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path);
        await using var hosted = await StartMcpGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var config = new WorkspaceMcpConfig
        {
            McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
            {
                ["my-tool"] = new WorkspaceMcpServerEntry
                {
                    Command = "npx",
                    Args = ["-y", "my-mcp-server@latest"],
                    Enabled = true,
                }
            }
        };

        var putResponse = await hosted.Client.PutAsJsonAsync("/api/mcp-servers", config, JsonOptions);
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await hosted.Client.GetAsync("/api/mcp-servers");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var readBack = await getResponse.Content.ReadFromJsonAsync<WorkspaceMcpConfig>(JsonOptions);

        readBack.Should().NotBeNull();
        readBack!.McpServers.Should().ContainKey("my-tool");
        readBack.McpServers["my-tool"].Command.Should().Be("npx");
        readBack.McpServers["my-tool"].Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Put_mcp_servers_should_require_token()
    {
        using var workspace = new McpTempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path);
        await using var hosted = await StartMcpGatewayAsync(workspace.Path);

        var response = await hosted.Client.PutAsJsonAsync("/api/mcp-servers", new WorkspaceMcpConfig(), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Test_connection_returns_failure_for_nonexistent_server()
    {
        using var workspace = new McpTempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path);
        await using var hosted = await StartMcpGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.PostAsync("/api/mcp-servers/no-such-server/test-connection", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<McpConnectionTestResultDto>(json, JsonOptions);
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    private static Task<HostedGateway> StartMcpGatewayAsync(string workspaceRoot)
    {
        return HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                });
            },
            useTestWorkspaceService: false);
    }

    private static async Task SeedWorkspaceAsync(string workspaceRoot)
    {
        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        using var provider = services.BuildServiceProvider();
        var workspace = provider.GetRequiredService<IWorkspaceService>();
        await workspace.EnsureInitializedAsync();
        await workspace.SaveAppConfigAsync(new WorkspaceAppConfig
        {
            WorkspaceVersion = KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
            BootstrapCompleted = true,
        });
    }

    private sealed class McpTempWorkspaceRoot : IDisposable
    {
        public McpTempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-mcp-api",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    // Minimal DTO for deserializing test-connection response
    private sealed class McpConnectionTestResultDto
    {
        public bool Success { get; init; }
        public int ToolCount { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
