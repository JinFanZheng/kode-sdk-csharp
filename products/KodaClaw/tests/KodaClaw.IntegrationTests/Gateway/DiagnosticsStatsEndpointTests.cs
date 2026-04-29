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
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

/// <summary>
/// L2 integration tests for GET /api/diagnostics/stats?since= (KC-6101).
/// </summary>
public sealed class DiagnosticsStatsEndpointTests
{
    [Fact]
    public async Task GetStats_NoSince_Returns200WithFields()
    {
        using var workspace = new DiagsTempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path);
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/diagnostics/stats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("totalEvents", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("errorCount", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("warningCount", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetStats_WithSince_Returns200()
    {
        using var workspace = new DiagsTempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path);
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var since = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        var response = await hosted.Client.GetAsync($"/api/diagnostics/stats?since={Uri.EscapeDataString(since)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("totalEvents", out var total).Should().BeTrue();
        total.GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GetStats_WithFutureSince_ReturnsZeroEvents()
    {
        using var workspace = new DiagsTempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path);
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var since = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
        var response = await hosted.Client.GetAsync($"/api/diagnostics/stats?since={Uri.EscapeDataString(since)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("totalEvents").GetInt32().Should().Be(0);
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

    private sealed class DiagsTempWorkspaceRoot : IDisposable
    {
        public DiagsTempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-diags-api",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
