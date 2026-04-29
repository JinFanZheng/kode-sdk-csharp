using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.Telegram;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Media;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Media;
using Moq;
using Xunit;

namespace KodaClaw.IntegrationTests.ChannelHub;

/// <summary>
/// KC-5603: Telegram sendAudio content-type routing tests.
/// Verifies that audio/* attachments route to SendAudioAsync and
/// image/* attachments continue to route to SendPhotoAsync (regression).
/// </summary>
public sealed class TelegramConnectorAudioTests
{
    private static ChannelAccount BuildAccount(string accountId = "telegram-audio-test")
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
            ConfigurationJson: """{"botToken":"audio-test-token"}""");
    }

    private static ChannelOutboundDraft BuildDraft(
        string accountId,
        IReadOnlyList<MediaReference>? mediaAttachments = null)
        => new(
            DraftId: "draft-audio-001",
            BindingId: "binding-001",
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: accountId,
            ExternalThreadId: "12345678",
            MessageText: "语音简报内容",
            DeliveryMode: DeliveryMode.AutoSend,
            CreatedAt: DateTimeOffset.UtcNow,
            MediaAttachments: mediaAttachments);

    // ── Audio routing ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_AudioAttachment_CallsSendAudioAsync()
    {
        var mockApiClient = new Mock<ITelegramApiClient>();
        mockApiClient
            .Setup(c => c.GetMeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramUser { Id = 1, IsBot = true, FirstName = "Bot", Username = "bot" });

        mockApiClient
            .Setup(c => c.SendAudioAsync(
                "audio-test-token", 12345678L,
                It.IsAny<Stream>(), "audio/mpeg", "语音简报内容",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramSendMessageResult { MessageId = 1001 });

        var mockMediaStore = new Mock<IMediaStore>();
        mockMediaStore
            .Setup(m => m.OpenReadAsync("media-mp3-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream)new MemoryStream([0x49, 0x44, 0x33])); // minimal mp3-like bytes

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
            new MediaReference("media-mp3-001", "audio/mpeg"),
        ]);

        await connector.SendAsync(draft);
        await connector.StopAsync(account.Id);

        mockApiClient.Verify(
            c => c.SendAudioAsync(
                "audio-test-token", 12345678L,
                It.IsAny<Stream>(), "audio/mpeg", "语音简报内容",
                It.IsAny<CancellationToken>()),
            Times.Once);

        mockApiClient.Verify(
            c => c.SendMessageAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendAsync_ImageAttachment_StillCallsSendPhotoAsync_Regression()
    {
        var mockApiClient = new Mock<ITelegramApiClient>();
        mockApiClient
            .Setup(c => c.GetMeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramUser { Id = 1, IsBot = true, FirstName = "Bot", Username = "bot" });

        mockApiClient
            .Setup(c => c.SendPhotoAsync(
                It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramSendMessageResult { MessageId = 1002 });

        var mockMediaStore = new Mock<IMediaStore>();
        mockMediaStore
            .Setup(m => m.OpenReadAsync("media-img-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream)new MemoryStream([0xFF, 0xD8, 0xFF])); // minimal jpg-like bytes

        var connector = new TelegramConnector(
            mockApiClient.Object,
            new TelegramConnectorOptions(
                IdleDelay: TimeSpan.FromMilliseconds(10),
                ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                IgnoreNonTextMessages: true),
            mediaStore: mockMediaStore.Object);

        var account = BuildAccount("telegram-img-regression");
        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        var draft = BuildDraft(account.Id, mediaAttachments:
        [
            new MediaReference("media-img-001", "image/jpeg"),
        ]);

        await connector.SendAsync(draft);
        await connector.StopAsync(account.Id);

        mockApiClient.Verify(
            c => c.SendPhotoAsync(
                It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<Stream>(), "image/jpeg", It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        mockApiClient.Verify(
            c => c.SendAudioAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendAsync_NoAttachment_CallsSendMessageAsync()
    {
        var mockApiClient = new Mock<ITelegramApiClient>();
        mockApiClient
            .Setup(c => c.GetMeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramUser { Id = 1, IsBot = true, FirstName = "Bot", Username = "bot" });
        mockApiClient
            .Setup(c => c.SendMessageAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelegramSendMessageResult { MessageId = 1003 });

        var connector = new TelegramConnector(
            mockApiClient.Object,
            new TelegramConnectorOptions(
                IdleDelay: TimeSpan.FromMilliseconds(10),
                ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                IgnoreNonTextMessages: true));

        var account = BuildAccount("telegram-text-only");
        await connector.StartAsync(account, (_, _) => Task.CompletedTask);
        await connector.SendAsync(BuildDraft(account.Id));
        await connector.StopAsync(account.Id);

        mockApiClient.Verify(
            c => c.SendMessageAsync("audio-test-token", 12345678L, "语音简报内容", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
