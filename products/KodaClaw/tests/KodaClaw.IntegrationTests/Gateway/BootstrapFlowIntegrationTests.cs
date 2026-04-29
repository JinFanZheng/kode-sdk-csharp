using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Bootstrap;
using KodaClaw.Contracts.Settings;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class BootstrapFlowIntegrationTests
{
    [Theory]
    [InlineData("", "# soul", "# user")]
    [InlineData("# identity", "", "# user")]
    [InlineData("# identity", "# soul", "")]
    public async Task Bootstrap_complete_returns_bad_request_when_markdown_missing(
        string identityMarkdown,
        string soulMarkdown,
        string userMarkdown)
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/system/bootstrap-complete",
            new
            {
                identityMarkdown,
                soulMarkdown,
                userMarkdown
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        error.Should().NotBeNull();
        error!.Code.Should().Be("validation.markdown_required");
    }

    [Fact]
    public async Task Bootstrap_complete_requires_token()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/system/bootstrap-complete",
            new
            {
                identityMarkdown = "# identity",
                soulMarkdown = "# soul",
                userMarkdown = "# user"
            });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Bootstrap_complete_writes_identity_and_user_and_switches_bootstrap_state()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var identityMarkdown = "# identity\n- Name: Koda";
        var soulMarkdown = "# soul\n- Rule: protect trust";
        var userMarkdown = "# user\n- Boundaries: direct";

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/system/bootstrap-complete",
            new
            {
                identityMarkdown,
                soulMarkdown,
                userMarkdown,
                archiveBootstrapFile = true
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<BootstrapCompletionResult>();
        result.Should().NotBeNull();
        result!.WorkspaceRootPath.Should().Be(workspace.Path);
        result.BootstrapCompleted.Should().BeTrue();
        result.BootstrapFileArchived.Should().BeTrue();
        File.ReadAllText(result.IdentityFilePath).Should().Be(identityMarkdown);
        File.ReadAllText(result.SoulFilePath).Should().Be(soulMarkdown);
        File.ReadAllText(result.UserFilePath).Should().Be(userMarkdown);

        var bootstrapPath = Path.Combine(
            workspace.Path,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            KodaClawWorkspaceLayout.BootstrapFile);
        File.Exists(bootstrapPath).Should().BeFalse();
        File.Exists(bootstrapPath + ".archived").Should().BeTrue();

        var snapshotResponse = await hosted.Client.GetAsync("/api/system/bootstrap-state");
        snapshotResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var snapshot = await snapshotResponse.Content.ReadFromJsonAsync<BootstrapStateResponse>();
        snapshot.Should().NotBeNull();
        snapshot!.RequiresBootstrap.Should().BeFalse();
        snapshot.Mode.Should().Be(AppMode.Normal);
    }

    private static Task<HostedGateway> StartRealWorkspaceGatewayAsync(string workspaceRoot)
    {
        return HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: true,
                rootPath: workspaceRoot),
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot
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
                "kodaclaw-bootstrap-flow",
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
