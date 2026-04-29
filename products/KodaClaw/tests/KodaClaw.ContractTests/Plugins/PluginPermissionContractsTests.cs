using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using KodaClaw.PluginHost.Permissions;
using Xunit;

namespace KodaClaw.ContractTests.Plugins;

public sealed class PluginPermissionContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Permission_set_should_json_round_trip()
    {
        var payload = new PluginPermissionSet(
            Filesystem: ["workspace/channels/telegram"],
            Network: true,
            Notifications: true,
            Background: false,
            Channels: ["telegram"],
            UiPanels: ["telegram-settings"],
            Secrets: ["telegram.botToken"]);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<PluginPermissionSet>(json, JsonOptions);

        json.Should().Contain("\"network\":true");
        roundTrip.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public void Normalization_should_trim_and_dedupe_permission_lists()
    {
        var permissions = new PluginPermissionSet(
            Filesystem: [" workspace/channels/telegram ", "workspace/channels/telegram", "./workspace/channels/telegram"],
            Channels: [" telegram ", "telegram"],
            UiPanels: [" panel-a ", "panel-a"],
            Secrets: [" telegram.botToken ", "telegram.botToken"]);

        var normalized = PluginPermissionPolicy.Normalize(permissions);

        normalized.Filesystem.Should().BeEquivalentTo(["workspace/channels/telegram"]);
        normalized.Channels.Should().BeEquivalentTo(["telegram"]);
        normalized.UiPanels.Should().BeEquivalentTo(["panel-a"]);
        normalized.Secrets.Should().BeEquivalentTo(["telegram.botToken"]);
    }

    [Fact]
    public void Risk_summary_should_mark_high_risk_for_network_background_and_secrets()
    {
        var permissions = new PluginPermissionSet(
            Network: true,
            Background: true,
            Secrets: ["telegram.botToken"],
            Notifications: true);

        var summary = PluginPermissionPolicy.SummarizeRisk(permissions);

        summary.HasHighRisk.Should().BeTrue();
        summary.HighRiskReasons.Should().Contain(reason => reason.Contains("network", StringComparison.OrdinalIgnoreCase));
        summary.HighRiskReasons.Should().Contain(reason => reason.Contains("background", StringComparison.OrdinalIgnoreCase));
        summary.HighRiskReasons.Should().Contain(reason => reason.Contains("secret", StringComparison.OrdinalIgnoreCase));
        summary.MediumRiskReasons.Should().Contain(reason => reason.Contains("notifications", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validation_should_reject_absolute_and_traversal_filesystem_paths()
    {
        var permissions = new PluginPermissionSet(
            Filesystem:
            [
                "/tmp/telegram",
                "workspace/../secrets",
            ]);

        var result = PluginPermissionPolicy.Validate(permissions);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("must be relative", StringComparison.OrdinalIgnoreCase));
        result.Errors.Should().Contain(error => error.Contains("must not contain traversal", StringComparison.OrdinalIgnoreCase));
    }
}
