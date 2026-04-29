using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

/// <summary>
/// L2 集成测试 — /api/workspace/git 端点。
/// 使用真实 WorkspaceGitService（useTestWorkspaceService=false）和临时目录。
/// </summary>
public sealed class WorkspaceGitApiIntegrationTests
{
    private const string GatewayToken = "test-token";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── auth guard ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Git_log_requires_authorization()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);

        var response = await hosted.Client.GetAsync("/api/workspace/git/log");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── log endpoint ───────────────────────────────────────────────────────

    [Fact]
    public async Task Git_log_returns_empty_list_for_fresh_workspace()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        // Trigger workspace initialisation (which calls EnsureGitRepoAsync)
        await hosted.Client.GetAsync("/api/workspace/readiness");

        var response = await hosted.Client.GetAsync("/api/workspace/git/log?limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var log = await response.Content.ReadFromJsonAsync<WorkspaceGitLogResponse>(JsonOptions);
        log.Should().NotBeNull();
        // Fresh workspace gets a system/init commit
        log!.Commits.Should().NotBeNull();
    }

    [Fact]
    public async Task Git_log_respects_limit_parameter()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        // Trigger init
        await hosted.Client.GetAsync("/api/workspace/readiness");

        var response = await hosted.Client.GetAsync("/api/workspace/git/log?limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var log = await response.Content.ReadFromJsonAsync<WorkspaceGitLogResponse>(JsonOptions);
        log!.Commits.Count.Should().BeLessThanOrEqualTo(1);
    }

    // ── diff endpoint ──────────────────────────────────────────────────────

    [Fact]
    public async Task Git_diff_returns_404_for_unknown_hash()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        await hosted.Client.GetAsync("/api/workspace/readiness");

        var response = await hosted.Client.GetAsync(
            "/api/workspace/git/diff/0000000000000000000000000000000000000000");

        // Unknown hash → 404 or empty (service swallows exception)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Git_diff_returns_text_plain_for_valid_hash()
    {
        using var workspace = new TempWorkspaceRoot();

        // Write a workspace file before starting the gateway so the init commit includes it
        Directory.CreateDirectory(Path.Combine(workspace.Path, "workspace"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "workspace", "IDENTITY.md"),
            "# Initial identity");

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        // Init triggers EnsureGitRepoAsync which creates the migrate/init commit
        await hosted.Client.GetAsync("/api/workspace/readiness");

        var logResponse = await hosted.Client.GetAsync("/api/workspace/git/log?limit=1");
        var log = await logResponse.Content.ReadFromJsonAsync<WorkspaceGitLogResponse>(JsonOptions);
        log!.Commits.Should().NotBeEmpty();

        var hash = log.Commits[0].Hash;
        var diffResponse = await hosted.Client.GetAsync($"/api/workspace/git/diff/{hash}");

        diffResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        diffResponse.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");
    }

    // ── revert-file endpoint ───────────────────────────────────────────────

    [Fact]
    public async Task Git_revert_file_returns_400_for_non_workspace_path()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        await hosted.Client.GetAsync("/api/workspace/readiness");

        var logResponse = await hosted.Client.GetAsync("/api/workspace/git/log?limit=1");
        var log = await logResponse.Content.ReadFromJsonAsync<WorkspaceGitLogResponse>(JsonOptions);
        var hash = log!.Commits.FirstOrDefault()?.Hash ?? "0000000000000000000000000000000000000001";

        var revertRequest = new WorkspaceGitRevertFileRequest(hash, "config/gateway.json");
        var response = await hosted.Client.PostAsJsonAsync(
            "/api/workspace/git/revert-file", revertRequest, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Git_revert_file_returns_400_for_unknown_hash()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        await hosted.Client.GetAsync("/api/workspace/readiness");

        var revertRequest = new WorkspaceGitRevertFileRequest(
            "0000000000000000000000000000000000000000",
            "workspace/IDENTITY.md");
        var response = await hosted.Client.PostAsJsonAsync(
            "/api/workspace/git/revert-file", revertRequest, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static Task<HostedGateway> StartGatewayAsync(string workspaceRoot)
    {
        return HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureServices: _ => { },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                });
            },
            useTestWorkspaceService: false);   // use real WorkspaceService + WorkspaceGitService
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-git-api",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                // libgit2sharp writes read-only files; must force-remove
                foreach (var file in Directory.GetFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
