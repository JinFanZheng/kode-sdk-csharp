using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.DingTalk;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

/// <summary>
/// Phase 0+1 钉钉发送逻辑测试：
/// - Phase 0：ThreadType 分支路由、sessionWebhook 缓存与 fallback
/// - Phase 1：Markdown 检测自动切换、ActionCard 单跳转、ActionCard 多按钮
/// </summary>
public sealed class DingTalkConnectorSendTests
{
    // ── Phase 0：群聊分支（SendGroupMessageInternalAsync）─────────────────

    [Fact]
    public async Task SendAsync_group_without_cached_webhook_should_call_SendGroupMessageAsync()
    {
        const string accountId = "acc-group-1";
        const string conversationId = "cid-group-1";
        var mockClient = new Mock<IDingTalkApiClient>();
        SetupOpenStream(mockClient);
        mockClient.Setup(c => c.GetAccessTokenAsync("ak_test", "secret_test", It.IsAny<CancellationToken>()))
            .ReturnsAsync("token-abc");

        var connector = BuildConnector(mockClient);
        await connector.StartAsync(BuildValidAccount(accountId), (_, _) => Task.CompletedTask);

        var draft = BuildDraft(
            accountId: accountId,
            externalThreadId: $"conversationId:{conversationId}",
            messageText: "hello group",
            threadType: ChannelThreadType.Group);

        await connector.SendAsync(draft);

        mockClient.Verify(c => c.SendGroupMessageAsync(
            "token-abc",
            "robot_test",
            conversationId,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        mockClient.Verify(c => c.SendSessionWebhookMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_group_with_valid_webhook_cache_should_call_SendSessionWebhookMessageAsync()
    {
        const string accountId = "acc-group-2";
        const string conversationId = "cid-group-2";
        const string webhookUrl = "https://oapi.dingtalk.com/robot/send?access_token=xxx";

        var mockClient = new Mock<IDingTalkApiClient>();
        SetupOpenStream(mockClient);

        var connector = BuildConnector(mockClient);
        await connector.StartAsync(BuildValidAccount(accountId), (_, _) => Task.CompletedTask);

        // 注入有效的 sessionWebhook（过期时间：5 分钟后）
        connector.SetConversationWebhookCache(
            conversationId, webhookUrl, DateTimeOffset.UtcNow.AddMinutes(5));

        var draft = BuildDraft(
            accountId: accountId,
            externalThreadId: $"conversationId:{conversationId}",
            messageText: "webhook msg",
            threadType: ChannelThreadType.Group);

        await connector.SendAsync(draft);

        mockClient.Verify(c => c.SendSessionWebhookMessageAsync(
            webhookUrl,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // 使用了 webhook，不应调用 GetAccessToken 和 SendGroupMessage
        mockClient.Verify(c => c.GetAccessTokenAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mockClient.Verify(c => c.SendGroupMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_group_with_expired_webhook_should_fallback_to_SendGroupMessageAsync()
    {
        const string accountId = "acc-group-3";
        const string conversationId = "cid-group-3";
        const string expiredWebhookUrl = "https://oapi.dingtalk.com/robot/send?access_token=expired";

        var mockClient = new Mock<IDingTalkApiClient>();
        SetupOpenStream(mockClient);
        mockClient.Setup(c => c.GetAccessTokenAsync("ak_test", "secret_test", It.IsAny<CancellationToken>()))
            .ReturnsAsync("token-xyz");

        var connector = BuildConnector(mockClient);
        await connector.StartAsync(BuildValidAccount(accountId), (_, _) => Task.CompletedTask);

        // 注入已过期的 sessionWebhook（过期时间：30 秒后，不满足 1 分钟余量）
        connector.SetConversationWebhookCache(
            conversationId, expiredWebhookUrl, DateTimeOffset.UtcNow.AddSeconds(30));

        var draft = BuildDraft(
            accountId: accountId,
            externalThreadId: $"conversationId:{conversationId}",
            messageText: "fallback msg",
            threadType: ChannelThreadType.Group);

        await connector.SendAsync(draft);

        // 应 fallback 到 orgGroupSend
        mockClient.Verify(c => c.SendGroupMessageAsync(
            "token-xyz", "robot_test", conversationId,
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        mockClient.Verify(c => c.SendSessionWebhookMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Phase 1：直聊分支——Markdown 检测 ─────────────────────────────────

    [Fact]
    public async Task SendAsync_direct_with_plain_text_should_call_SendTextMessageAsync()
    {
        const string accountId = "acc-direct-plain";
        const string conversationId = "cid-direct-plain";

        var mockClient = new Mock<IDingTalkApiClient>();
        SetupOpenStream(mockClient);
        mockClient.Setup(c => c.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var connector = BuildConnector(mockClient);
        await connector.StartAsync(BuildValidAccount(accountId), (_, _) => Task.CompletedTask);
        connector.SetConversationUserCache(conversationId, "user-001");

        var draft = BuildDraft(
            accountId: accountId,
            externalThreadId: $"conversationId:{conversationId}",
            messageText: "这是普通文本",
            threadType: ChannelThreadType.DirectMessage);

        await connector.SendAsync(draft);

        mockClient.Verify(c => c.SendTextMessageAsync(
            "token", "robot_test",
            It.Is<IReadOnlyList<string>>(ids => ids.Contains("user-001")),
            "这是普通文本",
            It.IsAny<CancellationToken>()), Times.Once);

        mockClient.Verify(c => c.SendMarkdownMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_direct_with_double_hash_should_call_SendMarkdownMessageAsync()
    {
        const string accountId = "acc-direct-md1";
        const string conversationId = "cid-direct-md1";

        var mockClient = new Mock<IDingTalkApiClient>();
        SetupOpenStream(mockClient);
        mockClient.Setup(c => c.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var connector = BuildConnector(mockClient);
        await connector.StartAsync(BuildValidAccount(accountId), (_, _) => Task.CompletedTask);
        connector.SetConversationUserCache(conversationId, "user-001");

        var draft = BuildDraft(
            accountId: accountId,
            externalThreadId: $"conversationId:{conversationId}",
            messageText: "## 标题\n内容",
            threadType: ChannelThreadType.DirectMessage);

        await connector.SendAsync(draft);

        mockClient.Verify(c => c.SendMarkdownMessageAsync(
            "token", "robot_test",
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(),
            "## 标题\n内容",
            It.IsAny<CancellationToken>()), Times.Once);

        mockClient.Verify(c => c.SendTextMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_direct_with_bold_markdown_should_call_SendMarkdownMessageAsync()
    {
        const string accountId = "acc-direct-md2";
        const string conversationId = "cid-direct-md2";

        var mockClient = new Mock<IDingTalkApiClient>();
        SetupOpenStream(mockClient);
        mockClient.Setup(c => c.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var connector = BuildConnector(mockClient);
        await connector.StartAsync(BuildValidAccount(accountId), (_, _) => Task.CompletedTask);
        connector.SetConversationUserCache(conversationId, "user-001");

        var draft = BuildDraft(
            accountId: accountId,
            externalThreadId: $"conversationId:{conversationId}",
            messageText: "这是 **加粗** 的文字",
            threadType: ChannelThreadType.DirectMessage);

        await connector.SendAsync(draft);

        mockClient.Verify(c => c.SendMarkdownMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Phase 1：直聊分支——ActionCard 单跳转 ─────────────────────────────

    [Fact]
    public async Task SendAsync_direct_with_action_card_single_metadata_should_call_SendActionCardMessageAsync()
    {
        const string accountId = "acc-direct-ac1";
        const string conversationId = "cid-direct-ac1";
        const string metadataJson = """
            {
                "msgKey": "sampleActionCard",
                "title": "操作确认",
                "text": "请确认以下操作",
                "singleTitle": "点击确认",
                "singleURL": "https://example.com/confirm"
            }
            """;

        var mockClient = new Mock<IDingTalkApiClient>();
        SetupOpenStream(mockClient);
        mockClient.Setup(c => c.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var connector = BuildConnector(mockClient);
        await connector.StartAsync(BuildValidAccount(accountId), (_, _) => Task.CompletedTask);
        connector.SetConversationUserCache(conversationId, "user-001");

        var draft = BuildDraft(
            accountId: accountId,
            externalThreadId: $"conversationId:{conversationId}",
            messageText: "请确认以下操作",
            threadType: ChannelThreadType.DirectMessage,
            metadataJson: metadataJson);

        await connector.SendAsync(draft);

        mockClient.Verify(c => c.SendActionCardMessageAsync(
            "token", "robot_test",
            It.IsAny<IReadOnlyList<string>>(),
            "操作确认",
            "请确认以下操作",
            "点击确认",
            "https://example.com/confirm",
            It.IsAny<CancellationToken>()), Times.Once);

        mockClient.Verify(c => c.SendTextMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mockClient.Verify(c => c.SendMarkdownMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Phase 1：直聊分支——ActionCard 独立多按钮 ──────────────────────────

    [Fact]
    public async Task SendAsync_direct_with_action_card6_metadata_should_call_SendActionCard6MessageAsync()
    {
        const string accountId = "acc-direct-ac6";
        const string conversationId = "cid-direct-ac6";
        const string metadataJson = """
            {
                "msgKey": "sampleActionCard6",
                "title": "多选操作",
                "text": "请选择",
                "btns": [
                    {"title": "选项A", "actionURL": "https://example.com/a"},
                    {"title": "选项B", "actionURL": "https://example.com/b"}
                ]
            }
            """;

        var mockClient = new Mock<IDingTalkApiClient>();
        SetupOpenStream(mockClient);
        mockClient.Setup(c => c.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var connector = BuildConnector(mockClient);
        await connector.StartAsync(BuildValidAccount(accountId), (_, _) => Task.CompletedTask);
        connector.SetConversationUserCache(conversationId, "user-001");

        var draft = BuildDraft(
            accountId: accountId,
            externalThreadId: $"conversationId:{conversationId}",
            messageText: "请选择",
            threadType: ChannelThreadType.DirectMessage,
            metadataJson: metadataJson);

        await connector.SendAsync(draft);

        mockClient.Verify(c => c.SendActionCard6MessageAsync(
            "token", "robot_test",
            It.IsAny<IReadOnlyList<string>>(),
            "多选操作",
            "请选择",
            It.Is<IReadOnlyList<DingTalkActionCardBtn>>(btns =>
                btns.Count == 2
                && btns[0].Title == "选项A"
                && btns[1].Title == "选项B"),
            It.IsAny<CancellationToken>()), Times.Once);

        mockClient.Verify(c => c.SendTextMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mockClient.Verify(c => c.SendActionCardMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_direct_with_action_card_metadata_takes_precedence_over_markdown_detection()
    {
        const string accountId = "acc-direct-acmd";
        const string conversationId = "cid-direct-acmd";
        // 消息文本包含 ## 但同时有 ActionCard metadata，应优先走 ActionCard 路径
        const string metadataJson = """
            {
                "msgKey": "sampleActionCard",
                "title": "标题含 ## 标记",
                "text": "## 内容也有标记",
                "singleTitle": "跳转",
                "singleURL": "https://example.com"
            }
            """;

        var mockClient = new Mock<IDingTalkApiClient>();
        SetupOpenStream(mockClient);
        mockClient.Setup(c => c.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var connector = BuildConnector(mockClient);
        await connector.StartAsync(BuildValidAccount(accountId), (_, _) => Task.CompletedTask);
        connector.SetConversationUserCache(conversationId, "user-001");

        var draft = BuildDraft(
            accountId: accountId,
            externalThreadId: $"conversationId:{conversationId}",
            messageText: "## 内容也有标记",
            threadType: ChannelThreadType.DirectMessage,
            metadataJson: metadataJson);

        await connector.SendAsync(draft);

        mockClient.Verify(c => c.SendActionCardMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // 不应降级为 Markdown 发送
        mockClient.Verify(c => c.SendMarkdownMessageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_direct_without_user_cache_should_throw_InvalidOperationException()
    {
        const string accountId = "acc-direct-nocache";
        const string conversationId = "cid-no-user";

        var mockClient = new Mock<IDingTalkApiClient>();
        SetupOpenStream(mockClient);

        var connector = BuildConnector(mockClient);
        await connector.StartAsync(BuildValidAccount(accountId), (_, _) => Task.CompletedTask);
        // 不注入 user cache

        var draft = BuildDraft(
            accountId: accountId,
            externalThreadId: $"conversationId:{conversationId}",
            messageText: "hello",
            threadType: ChannelThreadType.DirectMessage);

        Func<Task> act = () => connector.SendAsync(draft);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*recipient userId not cached*");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static DingTalkConnector BuildConnector(Mock<IDingTalkApiClient> mockClient)
    {
        return new DingTalkConnector(
            logger: NullLogger<DingTalkConnector>.Instance,
            apiClient: mockClient.Object);
    }

    private static void SetupOpenStream(Mock<IDingTalkApiClient> mockClient)
    {
        mockClient
            .Setup(c => c.OpenStreamConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return new DingTalkOpenConnectionResponse();
            });
    }

    private static ChannelAccount BuildValidAccount(string accountId)
    {
        return new ChannelAccount(
            Id: accountId,
            ConnectorKind: ChannelConnectorKind.DingTalk,
            DisplayName: "DingTalk Bot",
            State: ChannelAccountState.Disconnected,
            CreatedAt: new DateTimeOffset(2026, 3, 30, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 30, 0, 0, 0, TimeSpan.Zero),
            ConfigurationJson: """{"appKey":"ak_test","appSecret":"secret_test","robotCode":"robot_test"}""");
    }

    private static ChannelOutboundDraft BuildDraft(
        string accountId,
        string externalThreadId,
        string messageText,
        ChannelThreadType threadType,
        string? metadataJson = null)
    {
        return new ChannelOutboundDraft(
            DraftId: $"draft-{Guid.NewGuid():N}",
            BindingId: "binding-1",
            ConnectorKind: ChannelConnectorKind.DingTalk,
            AccountId: accountId,
            ExternalThreadId: externalThreadId,
            MessageText: messageText,
            ThreadType: threadType,
            DeliveryMode: DeliveryMode.AutoSend,
            CreatedAt: DateTimeOffset.UtcNow,
            MetadataJson: metadataJson);
    }
}
