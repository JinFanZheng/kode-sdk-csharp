using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class SettingsApiIntegrationTests
{
    private const string GatewayToken = "test-token";

    [Fact]
    public async Task Settings_get_should_require_token()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);

        var response = await hosted.Client.GetAsync("/api/settings");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Settings_get_should_return_default_snapshot()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.GetAsync("/api/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<KodaClawSettings>();
        payload.Should().NotBeNull();
        payload!.DefaultLandingRoute.Should().Be("/chat");
        payload.Theme.Should().Be(ThemeMode.System);
        payload.RequireApprovalForExternalActions.Should().BeFalse();
    }

    [Fact]
    public async Task Settings_put_should_persist_snapshot()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var request = new KodaClawSettings(
            DefaultLandingRoute: "/inbox",
            Theme: ThemeMode.Dark,
            RequireApprovalForExternalActions: false,
            NotificationsEnabled: false,
            QuietHoursEnabled: true,
            QuietHoursStartLocalTime: "08:00",
            QuietHoursEndLocalTime: "22:00",
            UpdatedAt: DateTimeOffset.UnixEpoch);

        var response = await hosted.Client.PutAsJsonAsync("/api/settings", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<KodaClawSettings>();
        payload.Should().NotBeNull();
        payload!.DefaultLandingRoute.Should().Be("/inbox");
        payload.Theme.Should().Be(ThemeMode.Dark);
        payload.RequireApprovalForExternalActions.Should().BeFalse();
        payload.NotificationsEnabled.Should().BeFalse();
        payload.QuietHoursEnabled.Should().BeTrue();
        payload.QuietHoursStartLocalTime.Should().Be("08:00");
        payload.QuietHoursEndLocalTime.Should().Be("22:00");
        payload.UpdatedAt.Should().NotBe(DateTimeOffset.UnixEpoch);

        var fetched = await hosted.Client.GetFromJsonAsync<KodaClawSettings>("/api/settings");
        fetched.Should().Be(payload);
    }

    [Fact]
    public async Task Settings_put_should_reject_invalid_quiet_hours()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var request = new KodaClawSettings(
            DefaultLandingRoute: "/chat",
            Theme: ThemeMode.Light,
            RequireApprovalForExternalActions: true,
            NotificationsEnabled: true,
            QuietHoursEnabled: true,
            QuietHoursStartLocalTime: "25:00",
            QuietHoursEndLocalTime: "19:00",
            UpdatedAt: DateTimeOffset.UnixEpoch);

        var response = await hosted.Client.PutAsJsonAsync("/api/settings", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("validation.settings_invalid");
    }

    private static Task<HostedGateway> StartGatewayAsync(string workspaceRoot)
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
                "kodaclaw-settings-api",
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
