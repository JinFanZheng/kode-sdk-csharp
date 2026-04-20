using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.DingTalk;
using KodaClaw.Contracts;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

public sealed class DingTalkConnectorConfigurationTests
{
    // ── FromAccountAsync: happy paths ───────────────────────────────────────

    [Fact]
    public async Task FromAccountAsync_should_parse_appKey_appSecret_and_robotCode_from_configuration_json()
    {
        var account = BuildAccount(configJson: """{"appKey":"ak_abc","appSecret":"s3cr3t","robotCode":"robot_x"}""");

        var config = await DingTalkConnectorConfiguration.FromAccountAsync(account);

        config.AppKey.Should().Be("ak_abc");
        config.AppSecret.Should().Be("s3cr3t");
        config.RobotCode.Should().Be("robot_x");
        config.DefaultDeliveryMode.Should().BeNull();
    }

    [Fact]
    public async Task FromAccountAsync_should_fall_back_to_ExternalAccountId_for_appKey()
    {
        var account = BuildAccount(
            configJson: """{"appSecret":"s3cr3t","robotCode":"robot_x"}""",
            externalAccountId: "ak_fallback");

        var config = await DingTalkConnectorConfiguration.FromAccountAsync(account);

        config.AppKey.Should().Be("ak_fallback");
    }

    [Fact]
    public async Task FromAccountAsync_should_parse_defaultDeliveryMode_case_insensitive()
    {
        var account = BuildAccount(
            configJson: """{"appKey":"ak_x","appSecret":"sec","robotCode":"rc","defaultDeliveryMode":"AutoSend"}""");

        var config = await DingTalkConnectorConfiguration.FromAccountAsync(account);

        config.DefaultDeliveryMode.Should().Be(DeliveryMode.AutoSend);
    }

    [Fact]
    public async Task FromAccountAsync_should_ignore_unknown_defaultDeliveryMode_values()
    {
        var account = BuildAccount(
            configJson: """{"appKey":"ak_x","appSecret":"sec","robotCode":"rc","defaultDeliveryMode":"unknown_mode"}""");

        var config = await DingTalkConnectorConfiguration.FromAccountAsync(account);

        config.DefaultDeliveryMode.Should().BeNull();
    }

    // ── FromAccountAsync: credential reference via inline: prefix ─────────

    [Fact]
    public async Task FromAccountAsync_should_resolve_appSecret_from_inline_credentialReference_in_config()
    {
        var account = BuildAccount(
            configJson: """{"appKey":"ak_x","robotCode":"rc","credentialReference":"inline:resolved-inline"}""");

        var config = await DingTalkConnectorConfiguration.FromAccountAsync(account);

        config.AppSecret.Should().Be("resolved-inline");
    }

    // ── FromAccountAsync: validation errors ───────────────────────────────

    [Fact]
    public async Task FromAccountAsync_should_throw_for_wrong_connector_kind()
    {
        var account = new ChannelAccount(
            Id: "acc",
            ConnectorKind: ChannelConnectorKind.Telegram,
            DisplayName: "Telegram",
            State: ChannelAccountState.Disconnected,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        Func<Task> act = () => DingTalkConnectorConfiguration.FromAccountAsync(account);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*DingTalk connector cannot start account with connector kind*");
    }

    [Fact]
    public async Task FromAccountAsync_should_throw_when_appKey_is_missing()
    {
        var account = BuildAccount(configJson: """{"appSecret":"s3cr3t","robotCode":"rc"}""");

        Func<Task> act = () => DingTalkConnectorConfiguration.FromAccountAsync(account);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*appKey is required*");
    }

    [Fact]
    public async Task FromAccountAsync_should_throw_when_appSecret_cannot_be_resolved()
    {
        var account = BuildAccount(configJson: """{"appKey":"ak_x","robotCode":"rc"}""");

        Func<Task> act = () => DingTalkConnectorConfiguration.FromAccountAsync(account);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*appSecret is required*");
    }

    [Fact]
    public async Task FromAccountAsync_should_throw_when_robotCode_is_missing()
    {
        var account = BuildAccount(configJson: """{"appKey":"ak_x","appSecret":"sec"}""");

        Func<Task> act = () => DingTalkConnectorConfiguration.FromAccountAsync(account);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*robotCode is required*");
    }

    [Fact]
    public async Task FromAccountAsync_should_throw_for_non_object_configuration_json()
    {
        var account = BuildAccount(configJson: """["not","an","object"]""");

        Func<Task> act = () => DingTalkConnectorConfiguration.FromAccountAsync(account);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*DingTalk account configuration must be a JSON object*");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static ChannelAccount BuildAccount(
        string? configJson = null,
        string? externalAccountId = null,
        string? credentialReference = null)
    {
        return new ChannelAccount(
            Id: "dingtalk-main",
            ConnectorKind: ChannelConnectorKind.DingTalk,
            DisplayName: "DingTalk Bot",
            State: ChannelAccountState.Disconnected,
            CreatedAt: new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero),
            ConfigurationJson: configJson,
            ExternalAccountId: externalAccountId,
            CredentialReference: credentialReference);
    }
}
