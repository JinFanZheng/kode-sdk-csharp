using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Secrets;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

public sealed class SecretMigrationReportContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Secret_migration_report_should_json_round_trip()
    {
        var payload = new SecretMigrationReport(
            GeneratedAt: new DateTimeOffset(2026, 3, 19, 10, 0, 0, TimeSpan.Zero),
            WorkspaceRootPath: "/tmp/kodaclaw",
            ArtifactPath: "config/secret-migration-report.json",
            Summary: new SecretMigrationSummary(
                TotalCount: 3,
                MigratedCount: 1,
                LegacyFallbackCount: 1,
                MissingCount: 1),
            Items:
            [
                new SecretMigrationItem(
                    Id: "gateway-token",
                    Kind: "gatewayToken",
                    DisplayName: "Gateway access token",
                    State: SecretMigrationState.Migrated,
                    Location: "config/gateway",
                    Field: "token",
                    ConfiguredSecretRef: "keychain:gateway:desktop-token",
                    SecretRefExists: true,
                    LegacySource: "configurationValue:KODACLAW_GATEWAY_TOKEN",
                    LegacySourceAvailable: true,
                    Notes: "Legacy fallback remains configured."),
                new SecretMigrationItem(
                    Id: "channel-account:webhook-main",
                    Kind: "channelAccount",
                    DisplayName: "Channel 'Webhook' (GenericWebhook)",
                    State: SecretMigrationState.LegacyFallback,
                    Location: "channel_accounts/webhook-main",
                    Field: "configurationJson.sharedSecret",
                    LegacySource: "literalConfigValue",
                    LegacySourceAvailable: true),
                new SecretMigrationItem(
                    Id: "plugin-header:plugin.fixture:Authorization",
                    Kind: "pluginRuntimeHeader",
                    DisplayName: "Plugin 'Fixture' header 'Authorization'",
                    State: SecretMigrationState.Missing,
                    Location: "plugins/plugin.fixture",
                    Field: "runtime.headerReferences[Authorization]",
                    ConfiguredSecretRef: "keychain:plugins:fixture-auth",
                    SecretRefExists: false,
                    Notes: "Configured secret ref was not found in the secret store.")
            ]);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<SecretMigrationReport>(json, JsonOptions);

        json.Should().Contain("\"artifactPath\":\"config/secret-migration-report.json\"");
        json.Should().Contain("\"state\":\"Migrated\"");
        json.Should().Contain("\"legacyFallbackCount\":1");
        roundTrip.Should().BeEquivalentTo(payload);
    }
}
