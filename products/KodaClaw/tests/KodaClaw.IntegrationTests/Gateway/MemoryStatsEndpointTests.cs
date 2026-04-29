using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class MemoryStatsEndpointTests
{
    [Fact]
    public async Task GET_memory_stats_returns_counts()
    {
        using var workspace = new MemoryTempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path);

        // Seed file-based memory data
        SeedMemoryFiles(workspace.Path, activeCount: 3, dormantCount: 1, archivedCount: 2);

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/memory/stats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("activeCount").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("dormantCount").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("archivedCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task GET_memory_entries_returns_list()
    {
        using var workspace = new MemoryTempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path);

        SeedMemoryFiles(workspace.Path, activeCount: 2, dormantCount: 0, archivedCount: 0);

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/memory/entries?status=active");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("entries").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task POST_memory_entries_promote_unknown_key_returns_404()
    {
        using var workspace = new MemoryTempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path);

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.PostAsync("/api/memory/entries/nonexistent-key/promote", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Task<HostedGateway> StartGatewayAsync(string workspaceRoot)
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

    private static void SeedMemoryFiles(string workspaceRoot, int activeCount, int dormantCount, int archivedCount)
    {
        // Write MEMORY.md with active sections
        if (activeCount > 0)
        {
            var memoryContent = "# Long-Term Memory\n\n";
            for (var i = 0; i < activeCount; i++)
            {
                memoryContent += $"## Active Entry {i}\nSome content for entry {i}\n\n";
            }

            var memoryDir = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.WorkspaceDirectory);
            Directory.CreateDirectory(memoryDir);
            File.WriteAllText(Path.Combine(memoryDir, KodaClawWorkspaceLayout.MemoryFile), memoryContent);
        }

        // Write dormant files
        if (dormantCount > 0)
        {
            var dormantDir = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.MemoryDormantDirectory);
            Directory.CreateDirectory(dormantDir);
            for (var i = 0; i < dormantCount; i++)
            {
                File.WriteAllText(
                    Path.Combine(dormantDir, $"dormant-key-{i}.md"),
                    $"---\nstatus: dormant\npriority: standard\n---\n# Dormant Entry {i}\n");
            }
        }

        // Write archive files
        if (archivedCount > 0)
        {
            var archiveDir = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.MemoryArchiveDirectory);
            Directory.CreateDirectory(archiveDir);
            for (var i = 0; i < archivedCount; i++)
            {
                File.WriteAllText(
                    Path.Combine(archiveDir, $"archived-key-{i}.md"),
                    $"---\nstatus: archived\npriority: standard\n---\n# Archived Entry {i}\n");
            }
        }
    }

    private sealed class MemoryTempWorkspaceRoot : IDisposable
    {
        public MemoryTempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-memory-api",
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
}
