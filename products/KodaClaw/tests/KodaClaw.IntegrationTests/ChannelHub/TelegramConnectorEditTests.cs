using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.Telegram;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using Moq;
using Xunit;

namespace KodaClaw.IntegrationTests.ChannelHub;

/// <summary>
/// KC-7204: TelegramConnector edit-in-place support (progress indicator).
/// Verifies SupportsEdit, SendWithReceiptAsync captures message id, and EditAsync
/// dispatches to editMessageText with the started bot's token.
/// </summary>
public sealed class TelegramConnectorEditTests
{
    private static ChannelAccount BuildAccount(string accountId = "telegram-edit-test")
    {
        var now = DateTimeOffset.UtcNow;
        return new ChannelAccount(
            Id: accountId,
            ConnectorKind: ChannelConnectorKind.Telegram,
            DisplayName: "Edit Bot",
            State: ChannelAccountState.Connected,
            CreatedAt: now,
            UpdatedAt: now,
            CredentialReference: null,
            ConfigurationJson: """{"botToken":"edit-test-token"}""");
    }

    private static ChannelOutboundDraft BuildDraft(string accountId)
        => new(
            DraftId: "draft-edit-001",
            BindingId: "binding-edit-001",
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: accountId,
            ExternalThreadId: "7788",
            MessageText: "🔄 思考中…",
            DeliveryMode: DeliveryMode.AutoSend,
            CreatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void SupportsEdit_IsTrue()
    {
        var connector = new TelegramConnector(Mock.Of<ITelegramApiClient>());
        connector.SupportsEdit.Should().BeTrue();
    }

    [Fact]
    public async Task SendWithReceiptAsync_ReturnsMessageId()
    {
        var mockApiClient = new Mock<ITelegramApiClient>();
        mockApiClient
            .Setup(c => c.GetMeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramUser { Id = 1, IsBot = true, FirstName = "Bot", Username = "bot" });
        mockApiClient
            .Setup(c => c.SendMessageAsync(
                "edit-test-token", 7788L, "🔄 思考中…", null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramSendMessageResult { MessageId = 4242 });

        var connector = new TelegramConnector(
            mockApiClient.Object,
            new TelegramConnectorOptions(
                IdleDelay: TimeSpan.FromMilliseconds(10),
                ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                IgnoreNonTextMessages: true));

        var account = BuildAccount();
        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        var receipt = await connector.SendWithReceiptAsync(BuildDraft(account.Id));

        receipt.ExternalMessageId.Should().Be("4242");
        receipt.SentAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));

        await connector.StopAsync(account.Id);
    }

    [Fact]
    public async Task EditAsync_InvokesEditMessageTextWithStartedBotToken()
    {
        var mockApiClient = new Mock<ITelegramApiClient>();
        mockApiClient
            .Setup(c => c.GetMeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramUser { Id = 1, IsBot = true, FirstName = "Bot", Username = "bot" });
        mockApiClient
            .Setup(c => c.EditMessageTextAsync(
                "edit-test-token", 7788L, 4242L, "✓ 完成", null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramSendMessageResult { MessageId = 4242 });

        var connector = new TelegramConnector(
            mockApiClient.Object,
            new TelegramConnectorOptions(
                IdleDelay: TimeSpan.FromMilliseconds(10),
                ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                IgnoreNonTextMessages: true));

        var account = BuildAccount();
        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        await connector.EditAsync("7788", "4242", "✓ 完成", OutboundMessageFormat.PlainText);

        mockApiClient.Verify(
            c => c.EditMessageTextAsync(
                "edit-test-token", 7788L, 4242L, "✓ 完成", null,
                It.IsAny<CancellationToken>()),
            Times.Once);

        await connector.StopAsync(account.Id);
    }

    [Fact]
    public async Task EditAsync_MarkdownFormat_PassesMarkdownParseMode()
    {
        var mockApiClient = new Mock<ITelegramApiClient>();
        mockApiClient
            .Setup(c => c.GetMeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramUser { Id = 1, IsBot = true, FirstName = "Bot", Username = "bot" });
        mockApiClient
            .Setup(c => c.EditMessageTextAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramSendMessageResult { MessageId = 1 });

        var connector = new TelegramConnector(
            mockApiClient.Object,
            new TelegramConnectorOptions(
                IdleDelay: TimeSpan.FromMilliseconds(10),
                ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                IgnoreNonTextMessages: true));

        var account = BuildAccount();
        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        await connector.EditAsync("7788", "4242", "**bold**", OutboundMessageFormat.Markdown);

        mockApiClient.Verify(
            c => c.EditMessageTextAsync(
                It.IsAny<string>(), 7788L, 4242L, "**bold**", "Markdown",
                It.IsAny<CancellationToken>()),
            Times.Once);

        await connector.StopAsync(account.Id);
    }

    [Fact]
    public async Task EditAsync_WithoutStartedAccount_ThrowsInvalidOperation()
    {
        var mockApiClient = new Mock<ITelegramApiClient>();
        var connector = new TelegramConnector(mockApiClient.Object);

        var act = async () => await connector.EditAsync("7788", "4242", "hi", OutboundMessageFormat.PlainText);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no started accounts*");
    }

    [Fact]
    public async Task EditAsync_NonNumericThreadOrMessage_ThrowsArgumentException()
    {
        var mockApiClient = new Mock<ITelegramApiClient>();
        mockApiClient
            .Setup(c => c.GetMeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramUser { Id = 1, IsBot = true, FirstName = "Bot", Username = "bot" });

        var connector = new TelegramConnector(
            mockApiClient.Object,
            new TelegramConnectorOptions(
                IdleDelay: TimeSpan.FromMilliseconds(10),
                ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                IgnoreNonTextMessages: true));

        var account = BuildAccount();
        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        var badThread = async () => await connector.EditAsync("abc", "4242", "hi", OutboundMessageFormat.PlainText);
        await badThread.Should().ThrowAsync<ArgumentException>();

        var badMsg = async () => await connector.EditAsync("7788", "xyz", "hi", OutboundMessageFormat.PlainText);
        await badMsg.Should().ThrowAsync<ArgumentException>();

        await connector.StopAsync(account.Id);
    }
}
