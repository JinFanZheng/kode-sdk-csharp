using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.DingTalk;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

public sealed class DingTalkConnectorTests
{
    // ── Kind ─────────────────────────────────────────────────────────────

    [Fact]
    public void Kind_should_return_DingTalk()
    {
        var connector = BuildConnector();

        connector.Kind.Should().Be(ChannelConnectorKind.DingTalk);
    }

    // ── StartAsync: validation ────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_should_throw_for_wrong_connector_kind()
    {
        var connector = BuildConnector();
        var account = new ChannelAccount(
            Id: "acc",
            ConnectorKind: ChannelConnectorKind.Telegram,
            DisplayName: "Telegram",
            State: ChannelAccountState.Disconnected,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        Func<Task> act = () => connector.StartAsync(account, (_, _) => Task.CompletedTask);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*DingTalk connector cannot start account with connector kind*");
    }

    [Fact]
    public async Task StartAsync_should_throw_on_duplicate_start_for_same_account()
    {
        var connector = BuildConnector();
        var account = BuildValidAccount("dingtalk-dup");

        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        Func<Task> act = () => connector.StartAsync(account, (_, _) => Task.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already started*");
    }

    // ── StopAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task StopAsync_should_not_throw_when_account_is_not_started()
    {
        var connector = BuildConnector();

        Func<Task> act = () => connector.StopAsync("non-existent-account-id");

        await act.Should().NotThrowAsync();
    }

    // ── SendAsync: validation ─────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_should_throw_for_wrong_connector_kind()
    {
        var connector = BuildConnector();
        var draft = BuildDraft(
            accountId: "any",
            connectorKind: ChannelConnectorKind.Telegram,
            externalThreadId: "conversationId:123",
            messageText: "hello");

        Func<Task> act = () => connector.SendAsync(draft);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*DingTalk connector cannot send draft with connector kind*");
    }

    [Fact]
    public async Task SendAsync_should_throw_when_account_is_not_started()
    {
        var connector = BuildConnector();
        var draft = BuildDraft(
            accountId: "not-started",
            connectorKind: ChannelConnectorKind.DingTalk,
            externalThreadId: "conversationId:123",
            messageText: "hello");

        Func<Task> act = () => connector.SendAsync(draft);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be started before outbound delivery*");
    }

    [Fact]
    public async Task SendAsync_should_throw_when_ExternalThreadId_is_missing()
    {
        const string accountId = "dingtalk-send-test-1";
        var connector = BuildConnector();
        await connector.StartAsync(BuildValidAccount(accountId), (_, _) => Task.CompletedTask);

        var draft = BuildDraft(
            accountId: accountId,
            connectorKind: ChannelConnectorKind.DingTalk,
            externalThreadId: "",
            messageText: "hello");

        Func<Task> act = () => connector.SendAsync(draft);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*external thread id*");
    }

    [Fact]
    public async Task SendAsync_should_throw_when_MessageText_is_missing()
    {
        const string accountId = "dingtalk-send-test-2";
        var connector = BuildConnector();
        await connector.StartAsync(BuildValidAccount(accountId), (_, _) => Task.CompletedTask);

        var draft = BuildDraft(
            accountId: accountId,
            connectorKind: ChannelConnectorKind.DingTalk,
            externalThreadId: "conversationId:123",
            messageText: "");

        Func<Task> act = () => connector.SendAsync(draft);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*message text is required*");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static DingTalkConnector BuildConnector()
    {
        var mockApiClient = new Mock<IDingTalkApiClient>();
        mockApiClient
            .Setup(c => c.OpenStreamConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, CancellationToken ct) =>
            {
                // 模拟无限等待，直到取消，确保后台循环不会意外抛出
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return new DingTalkOpenConnectionResponse();
            });

        return new DingTalkConnector(
            logger: NullLogger<DingTalkConnector>.Instance,
            apiClient: mockApiClient.Object);
    }

    private static ChannelAccount BuildValidAccount(string accountId)
    {
        return new ChannelAccount(
            Id: accountId,
            ConnectorKind: ChannelConnectorKind.DingTalk,
            DisplayName: "DingTalk Bot",
            State: ChannelAccountState.Disconnected,
            CreatedAt: new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero),
            ConfigurationJson: """{"appKey":"ak_test","appSecret":"secret_test","robotCode":"robot_test"}""");
    }

    private static ChannelOutboundDraft BuildDraft(
        string accountId,
        ChannelConnectorKind connectorKind,
        string externalThreadId,
        string messageText)
    {
        return new ChannelOutboundDraft(
            DraftId: "draft-1",
            BindingId: "binding-1",
            ConnectorKind: connectorKind,
            AccountId: accountId,
            ExternalThreadId: externalThreadId,
            MessageText: messageText,
            DeliveryMode: DeliveryMode.AutoSend,
            CreatedAt: DateTimeOffset.UtcNow);
    }
}
