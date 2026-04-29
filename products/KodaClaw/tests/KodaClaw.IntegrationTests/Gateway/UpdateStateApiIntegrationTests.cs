using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.System;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class UpdateStateApiIntegrationTests
{
    private const string GatewayToken = "test-token";

    [Fact]
    public async Task Update_state_should_require_token()
    {
        using var workspace = new TempWorkspaceRoot();
        var manifestPath = await WriteFixtureManifestAsync(workspace.Path);
        await using var hosted = await StartGatewayAsync(workspace.Path, manifestPath);

        var response = await hosted.Client.GetAsync("/api/system/update-state");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Update_check_should_return_gateway_and_desktop_components_from_fixture_manifest()
    {
        using var workspace = new TempWorkspaceRoot();
        var manifestPath = await WriteFixtureManifestAsync(workspace.Path);
        await using var hosted = await StartGatewayAsync(workspace.Path, manifestPath);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/system/update-check",
            new UpdateCheckRequest(
                DesktopCurrentVersion: "0.1.0",
                DesktopReleaseChannel: UpdateReleaseChannel.Stable));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<UpdateStateResponse>();

        payload.Should().NotBeNull();
        payload!.ManifestSource.Should().Be(manifestPath);
        payload.Components.Should().HaveCount(2);
        payload.Components.Should().ContainSingle(item =>
            item.Component == "gateway" &&
            item.CurrentVersion == "0.1.0" &&
            item.LatestKnownVersion == "0.1.2" &&
            item.UpdateAvailability == UpdateAvailability.UpdateAvailable);
        payload.Components.Should().ContainSingle(item =>
            item.Component == "desktop" &&
            item.LatestKnownVersion == "0.1.3" &&
            item.UpdateAvailability == UpdateAvailability.UpdateAvailable);
        payload.OperatorNotes.Should().Contain(item => item.Contains("manual-first update flow", StringComparison.Ordinal));
        File.Exists(payload.ArtifactPath).Should().BeTrue();
    }

    [Fact]
    public async Task Update_state_should_return_persisted_snapshot_after_manual_check()
    {
        using var workspace = new TempWorkspaceRoot();
        var manifestPath = await WriteFixtureManifestAsync(workspace.Path);
        await using var hosted = await StartGatewayAsync(workspace.Path, manifestPath);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var checkedPayload = await hosted.Client.PostAsJsonAsync(
            "/api/system/update-check",
            new UpdateCheckRequest(
                DesktopCurrentVersion: "0.1.0",
                DesktopReleaseChannel: UpdateReleaseChannel.Stable));
        checkedPayload.EnsureSuccessStatusCode();
        var first = await checkedPayload.Content.ReadFromJsonAsync<UpdateStateResponse>();

        var persisted = await hosted.Client.GetFromJsonAsync<UpdateStateResponse>("/api/system/update-state");

        persisted.Should().NotBeNull();
        persisted!.GeneratedAt.Should().Be(first!.GeneratedAt);
        persisted.ArtifactPath.Should().Be(first.ArtifactPath);
        persisted.Components.Should().HaveCount(2);
    }

    [Fact]
    public async Task Update_check_should_drop_manifest_urls_with_non_http_schemes_and_emit_operator_notes()
    {
        using var workspace = new TempWorkspaceRoot();
        var manifestPath = await WriteFixtureManifestAsync(
            workspace.Path,
            downloadUrl: "file:///tmp/kodaclaw.pkg",
            releaseNotesUrl: "javascript:alert(1)");
        await using var hosted = await StartGatewayAsync(workspace.Path, manifestPath);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/system/update-check",
            new UpdateCheckRequest(
                DesktopCurrentVersion: "0.1.0",
                DesktopReleaseChannel: UpdateReleaseChannel.Stable));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<UpdateStateResponse>();

        payload.Should().NotBeNull();
        payload!.Components.Should().OnlyContain(item =>
            item.DownloadUrl == null &&
            item.ReleaseNotesUrl == null);
        payload.OperatorNotes.Should().Contain(item =>
            item.Contains("downloadUrl", StringComparison.Ordinal) &&
            item.Contains("http/https", StringComparison.Ordinal));
        payload.OperatorNotes.Should().Contain(item =>
            item.Contains("releaseNotesUrl", StringComparison.Ordinal) &&
            item.Contains("http/https", StringComparison.Ordinal));
    }

    private static Task<HostedGateway> StartGatewayAsync(string workspaceRoot, string manifestPath)
    {
        return HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                    ["KODACLAW_UPDATE_MANIFEST_PATH"] = manifestPath,
                    ["KODACLAW_UPDATE_RELEASE_CHANNEL"] = "Stable",
                });
            },
            useTestWorkspaceService: false);
    }

    private static async Task<string> WriteFixtureManifestAsync(
        string workspaceRoot,
        string downloadUrl = "https://example.com/download",
        string releaseNotesUrl = "https://example.com/release-notes")
    {
        var manifestPath = Path.Combine(workspaceRoot, "update-manifest.fixture.json");
        var escapedManifestPath = manifestPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            manifestPath,
            $$"""
            {
              "generatedAt": "2026-03-19T10:00:00Z",
              "source": "{{escapedManifestPath}}",
              "channels": [
                {
                  "channel": "Stable",
                  "latestVersion": "0.1.2",
                  "gatewayVersion": "0.1.2",
                  "desktopVersion": "0.1.3",
                  "downloadUrl": "{{downloadUrl}}",
                  "releaseNotesUrl": "{{releaseNotesUrl}}",
                  "releaseNotes": [
                    "Adds the manual-first update desk.",
                    "Improves operator release visibility."
                  ],
                  "guidance": "Review the release notes, then complete the guided handoff manually."
                }
              ]
            }
            """);
        return manifestPath;
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-update-state-api",
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
