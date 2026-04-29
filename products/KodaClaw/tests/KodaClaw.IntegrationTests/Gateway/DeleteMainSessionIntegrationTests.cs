using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

/// <summary>
/// L2 集成测试 — DELETE /api/sessions/main/{id}
/// 覆盖：400 invalid kind、400 active session、404 not found、200 success。
/// </summary>
public sealed class DeleteMainSessionIntegrationTests
{
    [Fact]
    public async Task Delete_should_require_token()
    {
        using var workspace = new TempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path, activeMainSessionId: null);
        await using var hosted = await StartGatewayAsync(workspace.Path);

        var response = await hosted.Client.DeleteAsync("/api/sessions/main/main-20260324-abc");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Delete_should_return_400_when_id_does_not_start_with_main_prefix()
    {
        using var workspace = new TempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path, activeMainSessionId: null);
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        // channel- 前缀应拒绝
        var response = await hosted.Client.DeleteAsync("/api/sessions/main/channel-dm-binding-001");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("session.delete.invalid_kind");
    }

    [Fact]
    public async Task Delete_should_return_400_when_deleting_active_main_session()
    {
        using var workspace = new TempWorkspaceRoot();
        const string activeId = "main-20260324120000-active0001";
        await SeedWorkspaceAsync(workspace.Path, activeMainSessionId: activeId);
        await SeedSessionAsync(workspace.Path, activeId);
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.DeleteAsync($"/api/sessions/main/{activeId}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("session.delete.active_session");
    }

    [Fact]
    public async Task Delete_should_return_404_when_session_directory_not_found()
    {
        using var workspace = new TempWorkspaceRoot();
        await SeedWorkspaceAsync(workspace.Path, activeMainSessionId: "main-20260324120000-active0001");
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.DeleteAsync("/api/sessions/main/main-20260320120000-ghost0001");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("session.not_found");
    }

    [Fact]
    public async Task Delete_should_return_200_and_remove_directory_for_non_active_session()
    {
        using var workspace = new TempWorkspaceRoot();
        const string activeId = "main-20260324120000-active0001";
        const string oldId    = "main-20260320120000-oldses0001";
        await SeedWorkspaceAsync(workspace.Path, activeMainSessionId: activeId);
        await SeedSessionAsync(workspace.Path, activeId);
        await SeedSessionAsync(workspace.Path, oldId);
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.DeleteAsync($"/api/sessions/main/{oldId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // 验证文件夹已被删除
        var sessionsRoot = Path.Combine(workspace.Path, KodaClawWorkspaceLayout.SessionsDirectory);
        Directory.Exists(Path.Combine(sessionsRoot, oldId)).Should().BeFalse();
        // 其他 session 不受影响
        Directory.Exists(Path.Combine(sessionsRoot, activeId)).Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task SeedWorkspaceAsync(string workspaceRoot, string? activeMainSessionId)
    {
        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        using var provider = services.BuildServiceProvider();

        var workspaceService = provider.GetRequiredService<IWorkspaceService>();
        await workspaceService.EnsureInitializedAsync();
        await workspaceService.SaveAppConfigAsync(new WorkspaceAppConfig
        {
            WorkspaceVersion = KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
            BootstrapCompleted = true,
            ActiveMainSessionId = activeMainSessionId,
        });
    }

    private static Task SeedSessionAsync(string workspaceRoot, string sessionId)
    {
        var sessionsRoot = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.SessionsDirectory);
        var dir = Path.Combine(sessionsRoot, sessionId);
        Directory.CreateDirectory(dir);
        return Task.CompletedTask;
    }

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

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-delete-session",
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
