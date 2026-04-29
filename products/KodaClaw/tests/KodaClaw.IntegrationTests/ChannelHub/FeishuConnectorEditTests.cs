using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.Feishu;
using KodaClaw.ChannelHub.Connectors.Feishu.Models;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.IntegrationTests.ChannelHub;

/// <summary>
/// KC-7204: FeishuConnector edit-in-place support (progress indicator).
/// Verifies SupportsEdit, SendWithReceiptAsync returns message id, and EditAsync
/// dispatches to PatchTextMessageAsync using a freshly-fetched tenant token.
/// </summary>
public sealed class FeishuConnectorEditTests
{
    private static ChannelAccount BuildAccount(string accountId = "feishu-edit-test")
    {
        var now = DateTimeOffset.UtcNow;
        return new ChannelAccount(
            Id: accountId,
            ConnectorKind: ChannelConnectorKind.Feishu,
            DisplayName: "Edit Bot",
            State: ChannelAccountState.Connected,
            CreatedAt: now,
            UpdatedAt: now,
            CredentialReference: null,
            ConfigurationJson: """{"appId":"cli_edit","appSecret":"edit_secret"}""");
    }

    private static ChannelOutboundDraft BuildDraft(string accountId)
        => new(
            DraftId: "draft-feishu-edit-001",
            BindingId: "binding-feishu-edit-001",
            ConnectorKind: ChannelConnectorKind.Feishu,
            AccountId: accountId,
            ExternalThreadId: "chat_id:oc_edit_chat",
            MessageText: "🔄 思考中…",
            DeliveryMode: DeliveryMode.AutoSend,
            CreatedAt: DateTimeOffset.UtcNow);

    private static Mock<IFeishuApiClient> BuildMockApiClient()
    {
        var mock = new Mock<IFeishuApiClient>();

        mock.Setup(c => c.GetTenantAccessTokenAsync(
                "cli_edit", "edit_secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync("tenant-token-v1");

        // WS endpoint + app token are exercised by FeishuWebSocketClient fire-and-forget
        // during StartAsync; stub just enough so it exits cleanly on error paths.
        mock.Setup(c => c.GetAppAccessTokenAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("app-token");

        mock.Setup(c => c.GetWsEndpointAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("WS endpoint disabled in unit test"));

        return mock;
    }

    [Fact]
    public void SupportsEdit_IsTrue()
    {
        var connector = new FeishuConnector(
            NullLogger<FeishuConnector>.Instance,
            Mock.Of<IFeishuApiClient>());
        connector.SupportsEdit.Should().BeTrue();
    }

    [Fact]
    public async Task SendWithReceiptAsync_ReturnsMessageIdFromApi()
    {
        var mockApi = BuildMockApiClient();
        mockApi.Setup(c => c.SendTextMessageAsync(
                "tenant-token-v1", "oc_edit_chat", "chat_id", "🔄 思考中…",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("om_feishu_message_42");

        var connector = new FeishuConnector(
            NullLogger<FeishuConnector>.Instance,
            mockApi.Object);

        var account = BuildAccount();
        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        var receipt = await connector.SendWithReceiptAsync(BuildDraft(account.Id));

        receipt.ExternalMessageId.Should().Be("om_feishu_message_42");
        receipt.SentAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));

        await connector.StopAsync(account.Id);
    }

    [Fact]
    public async Task EditAsync_CallsPatchTextMessageWithTenantToken()
    {
        var mockApi = BuildMockApiClient();
        mockApi.Setup(c => c.PatchTextMessageAsync(
                "tenant-token-v1", "om_feishu_message_42", "✓ 完成",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var connector = new FeishuConnector(
            NullLogger<FeishuConnector>.Instance,
            mockApi.Object);

        var account = BuildAccount();
        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        await connector.EditAsync(
            externalThreadId: "chat_id:oc_edit_chat",
            externalMessageId: "om_feishu_message_42",
            text: "✓ 完成",
            format: OutboundMessageFormat.PlainText);

        mockApi.Verify(
            c => c.PatchTextMessageAsync(
                "tenant-token-v1", "om_feishu_message_42", "✓ 完成",
                It.IsAny<CancellationToken>()),
            Times.Once);

        await connector.StopAsync(account.Id);
    }

    [Fact]
    public async Task EditAsync_WithoutStartedAccount_ThrowsInvalidOperation()
    {
        var mockApi = BuildMockApiClient();
        var connector = new FeishuConnector(
            NullLogger<FeishuConnector>.Instance,
            mockApi.Object);

        var act = async () => await connector.EditAsync(
            "chat_id:oc_x", "om_x", "hi", OutboundMessageFormat.PlainText);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no started accounts*");
    }

    [Fact]
    public async Task EditAsync_WithEmptyFields_ThrowsArgumentException()
    {
        var mockApi = BuildMockApiClient();
        var connector = new FeishuConnector(
            NullLogger<FeishuConnector>.Instance,
            mockApi.Object);

        var account = BuildAccount();
        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        var badThread = async () => await connector.EditAsync(
            "", "om_x", "hi", OutboundMessageFormat.PlainText);
        await badThread.Should().ThrowAsync<ArgumentException>();

        var badMsg = async () => await connector.EditAsync(
            "chat_id:oc_x", "  ", "hi", OutboundMessageFormat.PlainText);
        await badMsg.Should().ThrowAsync<ArgumentException>();

        var badText = async () => await connector.EditAsync(
            "chat_id:oc_x", "om_x", "", OutboundMessageFormat.PlainText);
        await badText.Should().ThrowAsync<ArgumentException>();

        await connector.StopAsync(account.Id);
    }
}
