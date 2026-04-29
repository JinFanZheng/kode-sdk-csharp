using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.Feishu;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

public sealed class FeishuConnectorConfigurationTests
{
    // ── FromAccountAsync: happy paths ───────────────────────────────────────

    [Fact]
    public async Task FromAccountAsync_should_parse_appId_and_appSecret_from_configuration_json()
    {
        var account = BuildAccount(configJson: """{"appId":"cli_abc","appSecret":"s3cr3t"}""");

        var config = await FeishuConnectorConfiguration.FromAccountAsync(account);

        config.AppId.Should().Be("cli_abc");
        config.AppSecret.Should().Be("s3cr3t");
        config.DefaultDeliveryMode.Should().BeNull();
    }

    [Fact]
    public async Task FromAccountAsync_should_fall_back_to_ExternalAccountId_for_appId()
    {
        var account = BuildAccount(
            configJson: """{"appSecret":"s3cr3t"}""",
            externalAccountId: "cli_fallback");

        var config = await FeishuConnectorConfiguration.FromAccountAsync(account);

        config.AppId.Should().Be("cli_fallback");
        config.AppSecret.Should().Be("s3cr3t");
    }

    [Fact]
    public async Task FromAccountAsync_should_parse_defaultDeliveryMode_case_insensitive()
    {
        var account = BuildAccount(
            configJson: """{"appId":"cli_x","appSecret":"sec","defaultDeliveryMode":"AutoSend"}""");

        var config = await FeishuConnectorConfiguration.FromAccountAsync(account);

        config.DefaultDeliveryMode.Should().Be(DeliveryMode.AutoSend);
    }

    [Fact]
    public async Task FromAccountAsync_should_ignore_unknown_defaultDeliveryMode_values()
    {
        var account = BuildAccount(
            configJson: """{"appId":"cli_x","appSecret":"sec","defaultDeliveryMode":"unknown_mode"}""");

        var config = await FeishuConnectorConfiguration.FromAccountAsync(account);

        config.DefaultDeliveryMode.Should().BeNull();
    }

    // ── FromAccountAsync: credential reference via inline: prefix ─────────

    [Fact]
    public async Task FromAccountAsync_should_resolve_appSecret_from_inline_credentialReference_in_config()
    {
        var account = BuildAccount(
            configJson: """{"appId":"cli_x","credentialReference":"inline:resolved-inline"}""");

        var config = await FeishuConnectorConfiguration.FromAccountAsync(account);

        config.AppSecret.Should().Be("resolved-inline");
    }

    [Fact]
    public async Task FromAccountAsync_should_resolve_appSecret_from_inline_credentialReference_on_account()
    {
        var account = BuildAccount(
            configJson: """{"appId":"cli_x"}""",
            credentialReference: "inline:resolved-from-account");

        var config = await FeishuConnectorConfiguration.FromAccountAsync(account);

        config.AppSecret.Should().Be("resolved-from-account");
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

        Func<Task> act = () => FeishuConnectorConfiguration.FromAccountAsync(account);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Feishu connector cannot start account with connector kind*");
    }

    [Fact]
    public async Task FromAccountAsync_should_throw_when_appId_is_missing()
    {
        var account = BuildAccount(configJson: """{"appSecret":"s3cr3t"}""");

        Func<Task> act = () => FeishuConnectorConfiguration.FromAccountAsync(account);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*app_id is required*");
    }

    [Fact]
    public async Task FromAccountAsync_should_throw_when_appSecret_cannot_be_resolved()
    {
        var account = BuildAccount(configJson: """{"appId":"cli_x"}""");

        Func<Task> act = () => FeishuConnectorConfiguration.FromAccountAsync(account);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*app_secret is required*");
    }

    [Fact]
    public async Task FromAccountAsync_should_throw_for_non_object_configuration_json()
    {
        var account = BuildAccount(configJson: """["not","an","object"]""");

        Func<Task> act = () => FeishuConnectorConfiguration.FromAccountAsync(account);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Feishu account configuration must be a JSON object*");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static ChannelAccount BuildAccount(
        string? configJson = null,
        string? externalAccountId = null,
        string? credentialReference = null)
    {
        return new ChannelAccount(
            Id: "feishu-main",
            ConnectorKind: ChannelConnectorKind.Feishu,
            DisplayName: "Feishu Bot",
            State: ChannelAccountState.Disconnected,
            CreatedAt: new DateTimeOffset(2026, 3, 22, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 22, 0, 0, 0, TimeSpan.Zero),
            ConfigurationJson: configJson,
            ExternalAccountId: externalAccountId,
            CredentialReference: credentialReference);
    }
}
