using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Automation;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Heartbeat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

/// <summary>
/// L2 集成测试 — 验证 kc CLI 依赖的 Gateway API 端点契约。
/// </summary>
public sealed class KcCliApiIntegrationTests
{
    [Fact]
    public async Task Automation_list_returns_items_array_with_kc_expected_shape()
    {
        using var workspace = new TempKcWorkspaceRoot();
        await SeedAsync(workspace.Path, CreateAutomation("heartbeat-daily"));
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/automations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("items", out var items).Should().BeTrue(
            "kc automation list --json expects { items: [...] }");
        items.ValueKind.Should().Be(JsonValueKind.Array);

        var first = items.EnumerateArray().First();
        first.TryGetProperty("id", out _).Should().BeTrue();
        first.TryGetProperty("title", out _).Should().BeTrue();
        first.TryGetProperty("cronExpression", out _).Should().BeTrue();
        first.TryGetProperty("enabled", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Automation_trigger_returns_success_status()
    {
        using var workspace = new TempKcWorkspaceRoot();
        await SeedAsync(workspace.Path, CreateAutomation("heartbeat-trigger"));
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var response = await hosted.Client.PostAsync("/api/automations/heartbeat-trigger/trigger", content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Automation_trigger_returns_404_for_unknown_id()
    {
        using var workspace = new TempKcWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var response = await hosted.Client.PostAsync("/api/automations/nonexistent-xyz/trigger", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "kc should exit with code 3 when automation not found");
    }

    [Fact]
    public async Task System_health_is_accessible_without_auth()
    {
        using var workspace = new TempKcWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);

        var response = await hosted.Client.GetAsync("/api/system/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "kc auth status relies on /api/system/health being auth-free");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("healthy");
    }

    [Fact]
    public async Task Workspace_readiness_returns_ready_field()
    {
        using var workspace = new TempKcWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/workspace/readiness");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("hasAnyGap", out _).Should().BeTrue(
            "kc workspace status --json expects { hasAnyGap: bool, isIdentitySet: bool, ... }");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task<HostedGateway> StartGatewayAsync(string workspaceRoot) =>
        HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureServices: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IHeartbeatSyncService>(
                    _ => new NoOpHeartbeatSyncService()));
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                });
            },
            useTestWorkspaceService: false);

    private static async Task SeedAsync(string workspaceRoot, AutomationDefinition definition)
    {
        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        services.AddKodaClawJsonStore(workspaceRoot);
        services.AddKodaClawAutomation();
        using var provider = services.BuildServiceProvider();
        var repo = provider.GetRequiredService<IAutomationDefinitionRepository>();
        await repo.UpsertAsync(definition);
    }

    private static AutomationDefinition CreateAutomation(string id) =>
        new(
            Id: id,
            Title: "KC Test Automation",
            Prompt: "Test prompt",
            Source: AutomationDefinitionSource.Heartbeat,
            SourcePath: "workspace/HEARTBEAT.md",
            CronExpression: "0 8 * * *",
            Enabled: true,
            InputPaths: null,
            ModelId: null,
            NotificationChannels: null,
            NotifyMode: AutomationNotifyMode.None,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            LastRunAt: null,
            NextRunAt: null,
            LastRunStatus: null,
            LastError: null);

    private sealed class NoOpHeartbeatSyncService : IHeartbeatSyncService
    {
        public Task<HeartbeatSyncResult> SyncAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new HeartbeatSyncResult(0, 0, false));
    }

    private sealed class TempKcWorkspaceRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "kodaclaw-kc-api", Guid.NewGuid().ToString("N"));

        public TempKcWorkspaceRoot() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
