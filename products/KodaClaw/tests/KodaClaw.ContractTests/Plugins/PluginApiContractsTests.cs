using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using Xunit;

namespace KodaClaw.ContractTests.Plugins;

public sealed class PluginApiContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Plugin_detail_should_json_round_trip()
    {
        var payload = new PluginDetail(
            Record: new PluginRecord(
                Id: "plugin.fixture",
                Manifest: new PluginManifest(
                    Id: "plugin.fixture",
                    Name: "Fixture Plugin",
                    Version: "0.1.0",
                    Types: [PluginType.Tool],
                    Runtime: new PluginRuntimeSpec(
                        Transport: PluginTransportKind.Stdio,
                        Command: "dotnet",
                        Args: ["fixture.dll"],
                        EnvironmentReferences: new Dictionary<string, string>
                        {
                            ["PLUGIN_API_TOKEN"] = "keychain:plugins:fixture-token"
                        },
                        HeaderReferences: new Dictionary<string, string>
                        {
                            ["Authorization"] = "env:PLUGIN_FIXTURE_AUTH"
                        }),
                    Permissions: new PluginPermissionSet(Network: true, Background: true),
                    Capabilities: new PluginCapabilitySet(Tools: ["echo"]),
                    Healthcheck: new PluginHealthcheckSpec(ToolName: "health_ping", TimeoutSeconds: 5)),
                InstallSource: PluginInstallSource.LocalDirectory,
                RootPath: "/tmp/plugin.fixture",
                TrustState: PluginTrustState.Trusted,
                Enabled: true,
                RuntimeState: PluginRuntimeState.Running,
                DiscoveredAt: new DateTimeOffset(2026, 3, 19, 0, 0, 0, TimeSpan.Zero),
                InstalledAt: new DateTimeOffset(2026, 3, 19, 0, 5, 0, TimeSpan.Zero),
                UpdatedAt: new DateTimeOffset(2026, 3, 19, 0, 6, 0, TimeSpan.Zero),
                LastHealthAt: new DateTimeOffset(2026, 3, 19, 0, 7, 0, TimeSpan.Zero),
                RestartCount: 1,
                TrustEvidence: new PluginTrustEvidence(
                    Source: PluginTrustEvidenceSource.SignatureSidecar,
                    VerificationState: PluginTrustVerificationState.Verified,
                    Summary: "Signature sidecar matched the current manifest and package digests.",
                    VerifiedAt: new DateTimeOffset(2026, 3, 19, 0, 6, 30, TimeSpan.Zero),
                    ManifestDigestSha256: "manifest-digest",
                    PackageDigestSha256: "package-digest",
                    Signer: "Fixture Publisher",
                    SignatureFilePath: "/tmp/plugin.fixture/plugin.signature.json")),
            PermissionSummary: new PluginPermissionRiskSummary(
                HighRiskReasons: ["Requests network access."],
                MediumRiskReasons: ["Can send notifications."]),
            HealthSummary: new PluginHealthSummary(
                Status: "Healthy",
                Message: "Plugin runtime is responding.",
                LastHealthAt: new DateTimeOffset(2026, 3, 19, 0, 7, 0, TimeSpan.Zero),
                RestartCount: 1),
            AvailableTools: ["mcp__plugin.fixture__echo"]);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<PluginDetail>(json, JsonOptions);

        json.Should().Contain("\"record\"");
        json.Should().Contain("\"permissionSummary\"");
        json.Should().Contain("\"availableTools\"");
        roundTrip.Should().NotBeNull();
        roundTrip!.Record.Id.Should().Be(payload.Record.Id);
        roundTrip.Record.Manifest.Runtime.EnvironmentReferences.Should().ContainKey("PLUGIN_API_TOKEN");
        roundTrip.Record.Manifest.Runtime.HeaderReferences.Should().ContainKey("Authorization");
        roundTrip.Record.TrustEvidence.Should().NotBeNull();
        roundTrip.Record.TrustEvidence!.VerificationState.Should().Be(PluginTrustVerificationState.Verified);
        roundTrip.Record.TrustEvidence.Signer.Should().Be("Fixture Publisher");
        roundTrip.PermissionSummary.HasHighRisk.Should().BeTrue();
        roundTrip.HealthSummary.IsHealthy.Should().BeTrue();
        roundTrip.AvailableTools.Should().ContainSingle().Which.Should().Be("mcp__plugin.fixture__echo");
    }

    [Fact]
    public void Install_local_plugin_request_should_json_round_trip()
    {
        var payload = new InstallLocalPluginRequest(Path: "/tmp/plugins/plugin.fixture");

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<InstallLocalPluginRequest>(json, JsonOptions);

        json.Should().Contain("\"path\":\"/tmp/plugins/plugin.fixture\"");
        roundTrip.Should().NotBeNull();
        roundTrip!.Path.Should().Be(payload.Path);
    }
}
