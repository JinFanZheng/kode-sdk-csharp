using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.Workspace;
using KodaClaw.IntegrationTests.PluginHost;
using KodaClaw.PluginHost.Trust;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class PluginApiIntegrationTests
{
    private const string GatewayToken = "test-token";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Plugins_list_should_require_token()
    {
        await using var fixture = await PluginHostFixture.CreateAsync();
        await using var hosted = await StartGatewayAsync(fixture.RootPath);

        var response = await hosted.Client.GetAsync("/api/plugins");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Plugin_detail_should_return_seeded_registry_projection()
    {
        await using var fixture = await PluginHostFixture.CreateAsync();
        await using var hosted = await StartGatewayAsync(fixture.RootPath);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);

        var listResponse = await hosted.Client.GetAsync("/api/plugins?limit=20");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var listPayload = await listResponse.Content.ReadFromJsonAsync<PluginsQueryResponse>();

        listPayload.Should().NotBeNull();
        listPayload!.Items.Should().ContainSingle(item => item.Id == fixture.PluginIdValue);

        var detailResponse = await hosted.Client.GetAsync($"/api/plugins/{fixture.PluginIdValue}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailPayload = await detailResponse.Content.ReadFromJsonAsync<PluginDetail>();

        detailPayload.Should().NotBeNull();
        detailPayload!.Record.Id.Should().Be(fixture.PluginIdValue);
        detailPayload.Record.TrustState.Should().Be(PluginTrustState.Trusted);
        detailPayload.Record.TrustEvidence.Should().NotBeNull();
        detailPayload.Record.TrustEvidence!.VerificationState.Should().Be(PluginTrustVerificationState.DigestOnly);
        detailPayload.Record.Enabled.Should().BeTrue();
        detailPayload.Record.RuntimeState.Should().Be(PluginRuntimeState.Stopped);
        detailPayload.HealthSummary.Status.Should().Be("Stopped");
        detailPayload.PermissionSummary.HighRiskReasons.Should().Contain(reason => reason.Contains("background", StringComparison.OrdinalIgnoreCase));
        detailPayload.AvailableTools.Should().ContainSingle().Which.Should().Be("mcp__plugin.fixture__echo");
    }

    [Fact]
    public async Task Plugin_state_actions_should_drive_trust_enable_start_stop_and_logs()
    {
        await using var fixture = await PluginHostFixture.CreateAsync();
        var seeded = await fixture.ReloadAsync();
        await fixture.Registry.UpsertAsync(seeded with
        {
            TrustState = PluginTrustState.Untrusted,
            Enabled = false,
            RuntimeState = PluginRuntimeState.Stopped,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await using var hosted = await StartGatewayAsync(fixture.RootPath);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);

        var trustResponse = await hosted.Client.PostAsync($"/api/plugins/{fixture.PluginIdValue}/trust", null);
        trustResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var trusted = await trustResponse.Content.ReadFromJsonAsync<PluginDetail>();
        trusted.Should().NotBeNull();
        trusted!.Record.TrustState.Should().Be(PluginTrustState.Trusted);

        var enableResponse = await hosted.Client.PostAsync($"/api/plugins/{fixture.PluginIdValue}/enable", null);
        enableResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var enabled = await enableResponse.Content.ReadFromJsonAsync<PluginDetail>();
        enabled.Should().NotBeNull();
        enabled!.Record.Enabled.Should().BeTrue();

        var startResponse = await hosted.Client.PostAsync($"/api/plugins/{fixture.PluginIdValue}/start", null);
        startResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var started = await startResponse.Content.ReadFromJsonAsync<PluginDetail>();
        started.Should().NotBeNull();
        started!.Record.RuntimeState.Should().Be(PluginRuntimeState.Running);
        started.AvailableTools.Should().Contain("mcp__plugin.fixture__echo");
        started.HealthSummary.Status.Should().Be("Healthy");

        var logsResponse = await hosted.Client.GetAsync($"/api/plugins/{fixture.PluginIdValue}/logs?limit=20");
        logsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var logs = await logsResponse.Content.ReadFromJsonAsync<List<PluginLogEntry>>();
        logs.Should().NotBeNull();
        logs!.Should().Contain(entry => entry.Message == "Plugin started successfully.");

        var stopResponse = await hosted.Client.PostAsync($"/api/plugins/{fixture.PluginIdValue}/stop", null);
        stopResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var stopped = await stopResponse.Content.ReadFromJsonAsync<PluginDetail>();
        stopped.Should().NotBeNull();
        stopped!.Record.RuntimeState.Should().Be(PluginRuntimeState.Stopped);

        var disableResponse = await hosted.Client.PostAsync($"/api/plugins/{fixture.PluginIdValue}/disable", null);
        disableResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var disabled = await disableResponse.Content.ReadFromJsonAsync<PluginDetail>();
        disabled.Should().NotBeNull();
        disabled!.Record.Enabled.Should().BeFalse();
        disabled.Record.RuntimeState.Should().Be(PluginRuntimeState.Stopped);
    }

    [Fact]
    public async Task Install_local_plugin_should_copy_directory_and_return_detail()
    {
        using var workspace = new TempPluginWorkspaceRoot();
        using var source = new TempPluginSourceRoot();
        await WritePluginManifestAsync(source.Path, "plugin.install", "Installable Plugin");

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.PostAsJsonAsync("/api/plugins/install-local", new InstallLocalPluginRequest(source.Path));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var payload = await response.Content.ReadFromJsonAsync<PluginDetail>();
        payload.Should().NotBeNull();
        payload!.Record.Id.Should().Be("plugin.install");
        payload.Record.InstallSource.Should().Be(PluginInstallSource.LocalDirectory);
        payload.Record.TrustState.Should().Be(PluginTrustState.Untrusted);
        payload.Record.Enabled.Should().BeFalse();
        payload.Record.RootPath.Should().Contain(Path.Combine("workspace", "plugins", "plugin.install"));
        File.Exists(Path.Combine(payload.Record.RootPath, "plugin.json")).Should().BeTrue();
    }

    [Fact]
    public async Task Discover_should_scan_workspace_plugin_roots()
    {
        using var workspace = new TempPluginWorkspaceRoot();
        var pluginRoot = Path.Combine(
            workspace.Path,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            "plugins",
            "plugin.discovered");
        Directory.CreateDirectory(pluginRoot);
        await WritePluginManifestAsync(pluginRoot, "plugin.discovered", "Discovered Plugin");

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.PostAsync("/api/plugins/discover", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<PluginsQueryResponse>();
        payload.Should().NotBeNull();
        payload!.Items.Should().ContainSingle(item => item.Id == "plugin.discovered");
    }

    [Fact]
    public async Task Trust_should_promote_verified_plugin_to_signed()
    {
        using var workspace = new TempPluginWorkspaceRoot();
        var pluginRoot = Path.Combine(
            workspace.Path,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            "plugins",
            "plugin.signed");
        Directory.CreateDirectory(pluginRoot);
        await WritePluginManifestAsync(pluginRoot, "plugin.signed", "Signed Plugin");
        await WritePluginSignatureAsync(pluginRoot, "Fixture Publisher");

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);

        var discoverResponse = await hosted.Client.PostAsync("/api/plugins/discover", null);
        discoverResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var trustResponse = await hosted.Client.PostAsync("/api/plugins/plugin.signed/trust", null);
        trustResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await trustResponse.Content.ReadFromJsonAsync<PluginDetail>();

        payload.Should().NotBeNull();
        payload!.Record.TrustState.Should().Be(PluginTrustState.Signed);
        payload.Record.TrustEvidence.Should().NotBeNull();
        payload.Record.TrustEvidence!.VerificationState.Should().Be(PluginTrustVerificationState.Verified);
        payload.Record.TrustEvidence.Signer.Should().Be("Fixture Publisher");
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

    private static async Task WritePluginManifestAsync(string rootPath, string id, string name)
    {
        Directory.CreateDirectory(rootPath);

        var manifest = new PluginManifest(
            Id: id,
            Name: name,
            Version: "0.1.0",
            Types: [PluginType.Tool],
            Runtime: new PluginRuntimeSpec(
                Transport: PluginTransportKind.Stdio,
                Command: "dotnet",
                Args: ["fake.dll"]),
            Permissions: new PluginPermissionSet(Network: true, Background: true),
            Capabilities: new PluginCapabilitySet(Tools: ["echo"]),
            Healthcheck: new PluginHealthcheckSpec(ToolName: "health_ping", TimeoutSeconds: 5));

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(rootPath, "plugin.json"), json);
    }

    private static async Task WritePluginSignatureAsync(string rootPath, string signer)
    {
        var evaluator = new PluginTrustEvaluator();
        var digestOnly = await evaluator.EvaluateAsync(rootPath);
        var payload = new
        {
            manifestDigestSha256 = digestOnly.ManifestDigestSha256,
            packageDigestSha256 = digestOnly.PackageDigestSha256,
            signer,
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await File.WriteAllTextAsync(
            Path.Combine(rootPath, PluginTrustEvaluator.SignatureSidecarFileName),
            json);
    }

    private sealed class TempPluginWorkspaceRoot : IDisposable
    {
        public TempPluginWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-plugin-api",
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

    private sealed class TempPluginSourceRoot : IDisposable
    {
        public TempPluginSourceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-plugin-source",
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
