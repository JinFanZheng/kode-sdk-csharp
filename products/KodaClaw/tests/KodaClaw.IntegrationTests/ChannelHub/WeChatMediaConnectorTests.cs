using System.Text;
using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.WeChat;
using KodaClaw.Contracts;
using KodaClaw.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KodaClaw.IntegrationTests.ChannelHub;

/// <summary>
/// L2 集成测试：WeChatConnector 媒体入站（type=2 图片 / type=4 文件）和出站（MediaAttachments）。
/// 使用 stub API client 和 stub CDN client，不做真实 HTTP 调用。
/// </summary>
public sealed class WeChatMediaConnectorTests
{
    private static ChannelAccount BuildAccount()
    {
        var now = DateTimeOffset.UtcNow;
        return new ChannelAccount(
            Id: "wechat-test",
            ConnectorKind: ChannelConnectorKind.WeChat,
            DisplayName: "Test WeChat",
            State: ChannelAccountState.Connected,
            CreatedAt: now,
            UpdatedAt: now,
            ConfigurationJson: """{"botToken":"test-token"}""");
    }

    private static KodaClawWorkspaceOptions BuildWorkspaceOptions(string tmpDir) =>
        new() { RootPath = tmpDir };

    // ── 入站：收到图片消息（type=2）────────────────────────────────────────────

    [Fact]
    public async Task PollLoop_should_download_image_and_attach_MediaReference()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            var imageBytes = Encoding.UTF8.GetBytes("fake-image-content");
            var cdnClient = new StubWeChatCdnClient(imageBytes);
            var mediaStore = new InMemoryMediaStore();

            var apiClient = new StubWeChatApiClient([
                new ILinkMessage
                {
                    MessageId = 1001,
                    FromUserId = "wxid_alice",
                    ContextToken = "ctx_001",
                    ItemList = [
                        new ILinkMessageItem
                        {
                            Type = 2, // image
                            ImageItem = new ILinkImageItem
                            {
                                Media = new ILinkMedia
                                {
                                    EncryptQueryParam = "enc_param_img",
                                    AesKey = Convert.ToBase64String(new byte[16]),
                                    EncryptType = 1
                                }
                            }
                        }
                    ]
                }
            ]);

            var connector = new WeChatConnector(
                apiClient, cdnClient,
                new WeChatAuthManager(),
                BuildWorkspaceOptions(tmpDir),
                NullLogger<WeChatConnector>.Instance,
                mediaStore: mediaStore);

            var received = new List<ChannelEventEnvelope>();
            var account = BuildAccount();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            await connector.StartAsync(account, (env, _) => { received.Add(env); return Task.CompletedTask; }, cts.Token);
            await Task.Delay(500, CancellationToken.None); // 等待轮询处理
            await connector.StopAsync(account.Id);

            received.Should().HaveCount(1);
            var env = received[0];
            env.MediaAttachments.Should().NotBeNullOrEmpty();
            env.MediaAttachments![0].ContentType.Should().Be("image/jpeg");
            mediaStore.Stored.Should().ContainKey(env.MediaAttachments![0].MediaId);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── 入站：收到文件消息（type=4）────────────────────────────────────────────

    [Fact]
    public async Task PollLoop_should_download_file_and_infer_content_type()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            var fileBytes = Encoding.UTF8.GetBytes("%PDF-1.4 fake pdf");
            var cdnClient = new StubWeChatCdnClient(fileBytes);
            var mediaStore = new InMemoryMediaStore();

            var apiClient = new StubWeChatApiClient([
                new ILinkMessage
                {
                    MessageId = 1002,
                    FromUserId = "wxid_bob",
                    ContextToken = "ctx_002",
                    ItemList = [
                        new ILinkMessageItem
                        {
                            Type = 4, // file
                            FileItem = new ILinkFileItem
                            {
                                FileName = "report.pdf",
                                Len = fileBytes.Length.ToString(),
                                Media = new ILinkMedia
                                {
                                    EncryptQueryParam = "enc_param_file",
                                    AesKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                                        Convert.ToHexString(new byte[16]).ToLowerInvariant())),
                                    EncryptType = 1
                                }
                            }
                        }
                    ]
                }
            ]);

            var connector = new WeChatConnector(
                apiClient, cdnClient,
                new WeChatAuthManager(),
                BuildWorkspaceOptions(tmpDir),
                NullLogger<WeChatConnector>.Instance,
                mediaStore: mediaStore);

            var received = new List<ChannelEventEnvelope>();
            var account = BuildAccount();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            await connector.StartAsync(account, (env, _) => { received.Add(env); return Task.CompletedTask; }, cts.Token);
            await Task.Delay(500, CancellationToken.None);
            await connector.StopAsync(account.Id);

            received.Should().HaveCount(1);
            var env = received[0];
            env.MediaAttachments.Should().NotBeNullOrEmpty();
            env.MediaAttachments![0].ContentType.Should().Be("application/pdf");
            env.MediaAttachments![0].FileName.Should().Be("report.pdf");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── 出站：发送图片附件 ─────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_with_image_attachment_should_call_sendmessage_with_image_item()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            var imageBytes = Encoding.UTF8.GetBytes("fake-image-bytes");
            var mediaStore = new InMemoryMediaStore();
            var mediaId = await mediaStore.PreloadAsync("test.jpg", "image/jpeg", imageBytes);

            var cdnClient = new StubWeChatCdnClient(imageBytes, returnEncryptQueryParam: "enc_out_001");
            var apiClient = new StubWeChatApiClient([]);

            var connector = new WeChatConnector(
                apiClient, cdnClient,
                new WeChatAuthManager(),
                BuildWorkspaceOptions(tmpDir),
                NullLogger<WeChatConnector>.Instance,
                mediaStore: mediaStore);

            var account = BuildAccount();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            // 必须先 start 才能 send
            await connector.StartAsync(account, (_, _) => Task.CompletedTask, cts.Token);

            // 模拟一条入站消息来建立 contextToken 缓存
            apiClient.EnqueueMessage(new ILinkMessage
            {
                MessageId = 9001,
                FromUserId = "wxid_target",
                ContextToken = "ctx_outbound",
                ItemList = [new ILinkMessageItem { Type = 1, TextItem = new ILinkTextItem { Text = "hi" } }]
            });
            await Task.Delay(200, CancellationToken.None);

            var draft = new ChannelOutboundDraft(
                DraftId: Guid.NewGuid().ToString(),
                BindingId: "binding-001",
                ConnectorKind: ChannelConnectorKind.WeChat,
                AccountId: account.Id,
                ExternalThreadId: "wxid_target",
                MessageText: "",
                MediaAttachments: [new MediaReference(mediaId, "image/jpeg", "test.jpg")]);

            await connector.SendAsync(draft, cts.Token);
            await connector.StopAsync(account.Id);

            apiClient.SentMediaItems.Should().HaveCount(1);
            apiClient.SentMediaItems[0].Type.Should().Be(2); // image
            apiClient.SentMediaItems[0].ImageItem.Should().NotBeNull();
            apiClient.SentMediaItems[0].ImageItem!.Media.Should().NotBeNull();
            cdnClient.UploadedCount.Should().Be(1);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── 出站：发送视频附件（type=5 video_item）─────────────────────────────────

    [Fact]
    public async Task SendAsync_with_video_attachment_should_call_sendmessage_with_video_item()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            // 16 字节原文 → PKCS7 pad 成 32 字节密文
            var videoBytes = Encoding.UTF8.GetBytes("fake-video-bytes");
            const int expectedEncryptedSize = 32;

            var mediaStore = new InMemoryMediaStore();
            var mediaId = await mediaStore.PreloadAsync("clip.mp4", "video/mp4", videoBytes);

            var cdnClient = new StubWeChatCdnClient(videoBytes, returnEncryptQueryParam: "enc_video_001");
            var apiClient = new StubWeChatApiClient([]);

            var connector = new WeChatConnector(
                apiClient, cdnClient,
                new WeChatAuthManager(),
                BuildWorkspaceOptions(tmpDir),
                NullLogger<WeChatConnector>.Instance,
                mediaStore: mediaStore);

            var account = BuildAccount();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            await connector.StartAsync(account, (_, _) => Task.CompletedTask, cts.Token);

            // 模拟入站消息建立 contextToken 缓存
            apiClient.EnqueueMessage(new ILinkMessage
            {
                MessageId = 9002,
                FromUserId = "wxid_target_v",
                ContextToken = "ctx_video_outbound",
                ItemList = [new ILinkMessageItem { Type = 1, TextItem = new ILinkTextItem { Text = "hi" } }]
            });
            await Task.Delay(200, CancellationToken.None);

            var draft = new ChannelOutboundDraft(
                DraftId: Guid.NewGuid().ToString(),
                BindingId: "binding-video-001",
                ConnectorKind: ChannelConnectorKind.WeChat,
                AccountId: account.Id,
                ExternalThreadId: "wxid_target_v",
                MessageText: "",
                MediaAttachments: [new MediaReference(mediaId, "video/mp4", "clip.mp4")]);

            await connector.SendAsync(draft, cts.Token);
            await connector.StopAsync(account.Id);

            apiClient.SentMediaItems.Should().HaveCount(1);
            var item = apiClient.SentMediaItems[0];
            item.Type.Should().Be(5); // ILinkMessageTypeVideo
            item.VideoItem.Should().NotBeNull();
            item.VideoItem!.Media.Should().NotBeNull();
            item.VideoItem!.Media!.EncryptQueryParam.Should().Be("enc_video_001");
            item.VideoItem!.VideoSize.Should().Be(expectedEncryptedSize);
            item.ImageItem.Should().BeNull();
            item.FileItem.Should().BeNull();

            apiClient.UploadUrlRequests.Should().ContainSingle();
            apiClient.UploadUrlRequests[0].MediaType.Should().Be(2); // ILinkMediaTypeVideo
            cdnClient.UploadedCount.Should().Be(1);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── 出站：单个媒体失败不阻断文字发送 ────────────────────────────────────────

    [Fact]
    public async Task SendAsync_should_send_text_even_if_media_upload_fails()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        try
        {
            var mediaStore = new InMemoryMediaStore();
            var mediaId = await mediaStore.PreloadAsync("x.png", "image/png", [1, 2, 3]);

            var cdnClient = new StubWeChatCdnClient([], shouldThrow: true);
            var apiClient = new StubWeChatApiClient([]);

            var connector = new WeChatConnector(
                apiClient, cdnClient,
                new WeChatAuthManager(),
                BuildWorkspaceOptions(tmpDir),
                NullLogger<WeChatConnector>.Instance,
                mediaStore: mediaStore);

            var account = BuildAccount();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await connector.StartAsync(account, (_, _) => Task.CompletedTask, cts.Token);

            var draft = new ChannelOutboundDraft(
                DraftId: Guid.NewGuid().ToString(),
                BindingId: "b-001",
                ConnectorKind: ChannelConnectorKind.WeChat,
                AccountId: account.Id,
                ExternalThreadId: "wxid_x",
                MessageText: "fallback text",
                MediaAttachments: [new MediaReference(mediaId, "image/png")]);

            await connector.SendAsync(draft, cts.Token);
            await connector.StopAsync(account.Id);

            apiClient.SentTexts.Should().ContainSingle("fallback text");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubWeChatApiClient(IEnumerable<ILinkMessage> initialMessages) : IWeChatApiClient
    {
        private readonly Queue<ILinkMessage> _queue = new(initialMessages);
        private bool _firstPoll = true;

        public List<string> SentTexts { get; } = [];
        public List<ILinkMessageItem> SentMediaItems { get; } = [];
        public List<ILinkGetUploadUrlRequest> UploadUrlRequests { get; } = [];

        public void EnqueueMessage(ILinkMessage msg) => _queue.Enqueue(msg);

        public Task<ILinkGetUpdatesResponse> GetUpdatesAsync(string syncBuf, CancellationToken ct = default)
        {
            if (_firstPoll && _queue.Count > 0)
            {
                _firstPoll = false;
                var msgs = new List<ILinkMessage>();
                while (_queue.TryDequeue(out var m)) msgs.Add(m);
                return Task.FromResult(new ILinkGetUpdatesResponse { Msgs = msgs, GetUpdatesBuf = "buf_1" });
            }
            // 后续返回空，让 poll loop 挂起直到取消
            return Task.Delay(60_000, ct)
                .ContinueWith(_ => new ILinkGetUpdatesResponse { Msgs = [], GetUpdatesBuf = syncBuf },
                    TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        public Task SendTextAsync(string toUserId, string contextToken, string text, CancellationToken ct = default)
        {
            SentTexts.Add(text);
            return Task.CompletedTask;
        }

        public Task SendMediaAsync(string toUserId, string contextToken,
            IReadOnlyList<ILinkMessageItem> items, CancellationToken ct = default)
        {
            SentMediaItems.AddRange(items);
            return Task.CompletedTask;
        }

        public Task<ILinkGetUploadUrlResponse> GetUploadUrlAsync(
            ILinkGetUploadUrlRequest request, CancellationToken ct = default)
        {
            UploadUrlRequests.Add(request);
            return Task.FromResult(new ILinkGetUploadUrlResponse
            {
                UploadParam = new ILinkUploadParam { Url = "https://stub-cdn/upload" }
            });
        }

        public Task<ILinkQrCodeResponse> GetQrCodeAsync(CancellationToken ct = default) =>
            Task.FromResult(new ILinkQrCodeResponse());
        public Task<ILinkQrCodeStatusResponse> GetQrCodeStatusAsync(string qrcode, CancellationToken ct = default) =>
            Task.FromResult(new ILinkQrCodeStatusResponse { Status = "wait" });
        public Task<ILinkLoginStatusResponse> CheckLoginStatusAsync(string botToken, CancellationToken ct = default) =>
            Task.FromResult(new ILinkLoginStatusResponse { Ret = 0 });
        public Task<ILinkGetConfigResponse> GetConfigAsync(string ilinkUserId, string contextToken, CancellationToken ct = default) =>
            Task.FromResult(new ILinkGetConfigResponse { Ret = 0 }); // 无 ticket → 跳过 typing
        public Task SendTypingAsync(string ilinkUserId, string typingTicket, int status, CancellationToken ct = default) =>
            Task.CompletedTask;
        public void SetBotToken(string botToken) { }
    }

    private sealed class StubWeChatCdnClient(
        byte[] downloadContent,
        string returnEncryptQueryParam = "enc_stub",
        bool shouldThrow = false) : IWeChatCdnClient
    {
        public int UploadedCount { get; private set; }

        public Task<Stream> DownloadAndDecryptAsync(
            string cdnBaseUrl, string encryptQueryParam, string aesKeyBase64,
            bool isImage, CancellationToken ct)
        {
            return Task.FromResult<Stream>(new MemoryStream(downloadContent));
        }

        public Task<string> UploadEncryptedAsync(
            ILinkUploadParam uploadParam, byte[] encryptedBytes, CancellationToken ct)
        {
            if (shouldThrow) throw new HttpRequestException("CDN upload failed (stub)");
            UploadedCount++;
            return Task.FromResult(returnEncryptQueryParam);
        }
    }

    private sealed class InMemoryMediaStore : IMediaStore
    {
        public Dictionary<string, (byte[] Data, string ContentType, string? FileName)> Stored { get; } = [];

        public async Task<string> PreloadAsync(string fileName, string contentType, byte[] data)
        {
            var meta = await StoreAsync(
                new MemoryStream(data), contentType, fileName);
            return meta.Id;
        }

        public Task<MediaMeta> StoreAsync(string fileName, string contentType, Stream data,
            CancellationToken cancellationToken = default)
            => StoreAsync(data, contentType, fileName, cancellationToken: cancellationToken);

        public async Task<MediaMeta> StoreAsync(Stream data, string contentType,
            string? fileName = null, string? source = null, string? externalMessageId = null,
            CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, cancellationToken);
            var bytes = ms.ToArray();
            var id = Guid.NewGuid().ToString("N");
            Stored[id] = (bytes, contentType, fileName);
            return new MediaMeta(id, fileName ?? string.Empty, contentType, bytes.Length, DateTimeOffset.UtcNow);
        }

        public Task<MediaMeta?> GetMetaAsync(string id, CancellationToken cancellationToken = default)
        {
            if (!Stored.TryGetValue(id, out var entry)) return Task.FromResult<MediaMeta?>(null);
            var meta = new MediaMeta(id, entry.FileName ?? string.Empty, entry.ContentType, entry.Data.Length, DateTimeOffset.UtcNow);
            return Task.FromResult<MediaMeta?>(meta);
        }

        public Task<Stream?> OpenReadAsync(string id, CancellationToken cancellationToken = default)
        {
            if (!Stored.TryGetValue(id, out var entry)) return Task.FromResult<Stream?>(null);
            return Task.FromResult<Stream?>(new MemoryStream(entry.Data));
        }

        public Task<MediaCleanupResult> CleanExpiredAsync(TimeSpan retention, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MediaCleanupResult(0, 0, 0));
        public Task<bool> PinAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Stored.ContainsKey(id));
        public Task<bool> UnpinAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Stored.ContainsKey(id));
    }
}
