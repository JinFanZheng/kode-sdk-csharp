using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.WeChat;
using KodaClaw.Contracts;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

public sealed class WeChatConnectorConfigurationTests
{
    // ── FromAccountAsync: happy paths ───────────────────────────────────────

    [Fact]
    public async Task FromAccountAsync_should_parse_botToken_from_configuration_json()
    {
        var account = BuildAccount(configJson: """{"botToken":"TOKEN_ABC"}""");

        var config = await WeChatConnectorConfiguration.FromAccountAsync(account, workspaceRootPath: "/tmp/ws");

        config.BotToken.Should().Be("TOKEN_ABC");
        config.StateDir.Should().Be("/tmp/ws/state/wechat/wechat-main");
    }

    [Fact]
    public async Task FromAccountAsync_should_resolve_botToken_from_inline_credentialReference_in_config()
    {
        var account = BuildAccount(configJson: """{"credentialReference":"inline:resolved-inline"}""");

        var config = await WeChatConnectorConfiguration.FromAccountAsync(account, workspaceRootPath: "/tmp/ws");

        config.BotToken.Should().Be("resolved-inline");
    }

    [Fact]
    public async Task FromAccountAsync_should_resolve_botToken_from_inline_credentialReference_on_account()
    {
        var account = BuildAccount(
            configJson: "{}",
            credentialReference: "inline:resolved-from-account");

        var config = await WeChatConnectorConfiguration.FromAccountAsync(account, workspaceRootPath: "/tmp/ws");

        config.BotToken.Should().Be("resolved-from-account");
    }

    [Fact]
    public async Task FromAccountAsync_should_prefer_config_json_botToken_over_credentialReference()
    {
        var account = BuildAccount(
            configJson: """{"botToken":"config-token"}""",
            credentialReference: "inline:account-token");

        var config = await WeChatConnectorConfiguration.FromAccountAsync(account, workspaceRootPath: "/tmp/ws");

        config.BotToken.Should().Be("config-token");
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

        Func<Task> act = () => WeChatConnectorConfiguration.FromAccountAsync(account, "/tmp/ws");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*WeChat connector cannot start account with connector kind*");
    }

    [Fact]
    public async Task FromAccountAsync_should_throw_when_botToken_cannot_be_resolved()
    {
        var account = BuildAccount(configJson: "{}");

        Func<Task> act = () => WeChatConnectorConfiguration.FromAccountAsync(account, "/tmp/ws");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*bot_token is required*");
    }

    [Fact]
    public async Task FromAccountAsync_should_throw_for_non_object_configuration_json()
    {
        var account = BuildAccount(configJson: """["not","an","object"]""");

        Func<Task> act = () => WeChatConnectorConfiguration.FromAccountAsync(account, "/tmp/ws");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*WeChat account configuration must be a JSON object*");
    }

    [Fact]
    public async Task FromAccountAsync_should_throw_when_configuration_json_is_null()
    {
        var account = BuildAccount(configJson: null);

        Func<Task> act = () => WeChatConnectorConfiguration.FromAccountAsync(account, "/tmp/ws");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static ChannelAccount BuildAccount(
        string? configJson = null,
        string? credentialReference = null)
    {
        return new ChannelAccount(
            Id: "wechat-main",
            ConnectorKind: ChannelConnectorKind.WeChat,
            DisplayName: "微信",
            State: ChannelAccountState.Disconnected,
            CreatedAt: new DateTimeOffset(2026, 3, 24, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 24, 0, 0, 0, TimeSpan.Zero),
            ConfigurationJson: configJson,
            CredentialReference: credentialReference);
    }
}
