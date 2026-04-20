using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.Telegram;
using KodaClaw.Contracts;
using KodaClaw.Workspace;
using Moq;
using Xunit;

namespace KodaClaw.IntegrationTests.ChannelHub;

/// <summary>
/// KC-BUG-7203: Telegram sendVideo content-type routing + duration fallback + upload-failure fallback.
/// </summary>
public sealed class TelegramConnectorVideoTests
{
    private static ChannelAccount BuildAccount(string accountId = "telegram-video-test")
    {
        var now = DateTimeOffset.UtcNow;
        return new ChannelAccount(
            Id: accountId,
            ConnectorKind: ChannelConnectorKind.Telegram,
            DisplayName: "Test Bot",
            State: ChannelAccountState.Connected,
            CreatedAt: now,
            UpdatedAt: now,
            CredentialReference: null,
            ConfigurationJson: """{"botToken":"video-test-token"}""");
    }

    private static ChannelOutboundDraft BuildDraft(
        string accountId,
        IReadOnlyList<MediaReference>? mediaAttachments = null)
        => new(
            DraftId: "draft-video-001",
            BindingId: "binding-001",
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: accountId,
            ExternalThreadId: "12345678",
            MessageText: "视频简报",
            DeliveryMode: DeliveryMode.AutoSend,
            CreatedAt: DateTimeOffset.UtcNow,
            MediaAttachments: mediaAttachments);

    [Fact]
    public async Task SendAsync_VideoAttachment_CallsSendVideoAsyncWithDurationSeconds()
    {
        var mockApiClient = new Mock<ITelegramApiClient>();
        mockApiClient
            .Setup(c => c.GetMeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramUser { Id = 1, IsBot = true, FirstName = "Bot", Username = "bot" });
        mockApiClient
            .Setup(c => c.SendVideoAsync(
                "video-test-token", 12345678L,
                It.IsAny<Stream>(), "video/mp4", "视频简报",
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramSendMessageResult { MessageId = 2001 });

        var mockMediaStore = new Mock<IMediaStore>();
        mockMediaStore
            .Setup(m => m.OpenReadAsync("media-mp4-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream)new MemoryStream([0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70]));

        var connector = new TelegramConnector(
            mockApiClient.Object,
            new TelegramConnectorOptions(
                IdleDelay: TimeSpan.FromMilliseconds(10),
                ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                IgnoreNonTextMessages: true),
            mediaStore: mockMediaStore.Object);

        var account = BuildAccount();
        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        var draft = BuildDraft(account.Id, mediaAttachments:
        [
            new MediaReference("media-mp4-001", "video/mp4", DurationMs: 8500),
        ]);

        await connector.SendAsync(draft);
        await connector.StopAsync(account.Id);

        // 8500ms → 8s（向下取整）
        mockApiClient.Verify(
            c => c.SendVideoAsync(
                "video-test-token", 12345678L,
                It.IsAny<Stream>(), "video/mp4", "视频简报",
                8,
                It.IsAny<CancellationToken>()),
            Times.Once);

        mockApiClient.Verify(
            c => c.SendMessageAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendAsync_VideoAttachmentWithoutDuration_FallsBackToMediaStoreMeta()
    {
        var mockApiClient = new Mock<ITelegramApiClient>();
        mockApiClient
            .Setup(c => c.GetMeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramUser { Id = 1, IsBot = true, FirstName = "Bot", Username = "bot" });
        mockApiClient
            .Setup(c => c.SendVideoAsync(
                It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramSendMessageResult { MessageId = 2002 });

        var mockMediaStore = new Mock<IMediaStore>();
        mockMediaStore
            .Setup(m => m.OpenReadAsync("media-mp4-002", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream)new MemoryStream([0x00]));
        mockMediaStore
            .Setup(m => m.GetMetaAsync("media-mp4-002", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMeta(
                Id: "media-mp4-002",
                FileName: "clip.mp4",
                ContentType: "video/mp4",
                SizeBytes: 1024,
                StoredAt: DateTimeOffset.UtcNow,
                DurationMs: 12000));

        var connector = new TelegramConnector(
            mockApiClient.Object,
            new TelegramConnectorOptions(
                IdleDelay: TimeSpan.FromMilliseconds(10),
                ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                IgnoreNonTextMessages: true),
            mediaStore: mockMediaStore.Object);

        var account = BuildAccount("telegram-video-meta");
        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        // Draft 不带 DurationMs —— 走 GetMetaAsync fallback
        var draft = BuildDraft(account.Id, mediaAttachments:
        [
            new MediaReference("media-mp4-002", "video/mp4"),
        ]);

        await connector.SendAsync(draft);
        await connector.StopAsync(account.Id);

        // 12000ms → 12s
        mockApiClient.Verify(
            c => c.SendVideoAsync(
                It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<Stream>(), "video/mp4", "视频简报",
                12,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendAsync_VideoUploadFailure_FallsBackToTextMessage()
    {
        var mockApiClient = new Mock<ITelegramApiClient>();
        mockApiClient
            .Setup(c => c.GetMeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramUser { Id = 1, IsBot = true, FirstName = "Bot", Username = "bot" });
        mockApiClient
            .Setup(c => c.SendVideoAsync(
                It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("upload blocked by telegram"));
        mockApiClient
            .Setup(c => c.SendMessageAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramSendMessageResult { MessageId = 2003 });

        var mockMediaStore = new Mock<IMediaStore>();
        mockMediaStore
            .Setup(m => m.OpenReadAsync("media-mp4-003", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream)new MemoryStream([0x00]));

        var connector = new TelegramConnector(
            mockApiClient.Object,
            new TelegramConnectorOptions(
                IdleDelay: TimeSpan.FromMilliseconds(10),
                ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                IgnoreNonTextMessages: true),
            mediaStore: mockMediaStore.Object);

        var account = BuildAccount("telegram-video-fail");
        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        var draft = BuildDraft(account.Id, mediaAttachments:
        [
            new MediaReference("media-mp4-003", "video/mp4"),
        ]);

        await connector.SendAsync(draft);
        await connector.StopAsync(account.Id);

        // 视频失败后走文本 fallback，文本前缀带告警
        mockApiClient.Verify(
            c => c.SendMessageAsync(
                "video-test-token", 12345678L,
                It.Is<string>(t => t.Contains("视频发送失败") && t.Contains("视频简报")),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendAsync_NoMediaStore_SkipsVideoBranchAndSendsText()
    {
        var mockApiClient = new Mock<ITelegramApiClient>();
        mockApiClient
            .Setup(c => c.GetMeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramUser { Id = 1, IsBot = true, FirstName = "Bot", Username = "bot" });
        mockApiClient
            .Setup(c => c.SendMessageAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramSendMessageResult { MessageId = 2004 });

        var connector = new TelegramConnector(
            mockApiClient.Object,
            new TelegramConnectorOptions(
                IdleDelay: TimeSpan.FromMilliseconds(10),
                ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                IgnoreNonTextMessages: true));

        var account = BuildAccount("telegram-video-no-media-store");
        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        var draft = BuildDraft(account.Id, mediaAttachments:
        [
            new MediaReference("media-mp4-004", "video/mp4"),
        ]);

        await connector.SendAsync(draft);
        await connector.StopAsync(account.Id);

        mockApiClient.Verify(
            c => c.SendVideoAsync(
                It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        mockApiClient.Verify(
            c => c.SendMessageAsync("video-test-token", 12345678L, "视频简报", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
