using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Media;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KodaClaw.ChannelHub.Connectors.WeChat;

public sealed class WeChatConnector : IChannelConnector
{
    private const int ILinkErrCodeSessionExpired = -14;
    private const int ILinkMessageTypeText = 1;
    private const int ILinkMessageTypeImage = 2;
    private const int ILinkMessageTypeFile = 4;
    private const int ILinkMessageTypeVideo = 5;
    private const int ILinkMediaTypeImage = 1; // getuploadurl media_type
    private const int ILinkMediaTypeVideo = 2; // getuploadurl media_type
    private const int ILinkMediaTypeFile = 3;  // getuploadurl media_type

    // 正则：去除常见 Markdown 标记，微信不支持 Markdown
    private static readonly Regex MarkdownRegex = new(
        @"(\*\*|__)(.*?)\1|(\*|_)(.*?)\3|#{1,6}\s+|`{1,3}[^`]*`{1,3}|>\s+|!\[.*?\]\(.*?\)|\[([^\]]+)\]\([^)]+\)",
        RegexOptions.Compiled | RegexOptions.Singleline,
        TimeSpan.FromMilliseconds(200));

    private readonly ConcurrentDictionary<string, StartedAccount> _startedAccounts =
        new(StringComparer.Ordinal);

    // key = "accountId::fromUserId"，value = 最近一次收到的 contextToken
    // 发送回复时使用，避免 ThreadBinding 存储 contextToken 带来的 schema 变更
    private readonly ConcurrentDictionary<string, string> _contextTokenCache =
        new(StringComparer.Ordinal);

    private const string DiagnosticSource = "wechat";

    private readonly IWeChatApiClient _apiClient;
    private readonly IWeChatCdnClient _cdnClient;
    private readonly WeChatAuthManager _authManager;
    private readonly WeChatConnectorOptions _options;
    private readonly ChannelSecretResolver _secretResolver;
    private readonly IChannelAccountRepository? _accountRepository;
    private readonly IMediaStore? _mediaStore;
    private readonly ILogger<WeChatConnector> _logger;
    private readonly string _workspaceRootPath;
    private readonly IDiagnosticsService? _diagnosticsService;

    public WeChatConnector(
        IWeChatApiClient apiClient,
        IWeChatCdnClient cdnClient,
        WeChatAuthManager authManager,
        KodaClawWorkspaceOptions workspaceOptions,
        ILogger<WeChatConnector> logger,
        WeChatConnectorOptions? options = null,
        ISecretStore? secretStore = null,
        IChannelAccountRepository? accountRepository = null,
        IMediaStore? mediaStore = null,
        IDiagnosticsService? diagnosticsService = null)
    {
        _apiClient = apiClient;
        _cdnClient = cdnClient;
        _authManager = authManager;
        _workspaceRootPath = workspaceOptions.ResolveRootPath();
        _logger = logger;
        _options = options ?? new WeChatConnectorOptions();
        _secretResolver = new ChannelSecretResolver(secretStore);
        _accountRepository = accountRepository;
        _mediaStore = mediaStore;
        _diagnosticsService = diagnosticsService;
    }

    public ChannelConnectorKind Kind => ChannelConnectorKind.WeChat;

    public async Task StartAsync(
        ChannelAccount account,
        Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(onEvent);
        cancellationToken.ThrowIfCancellationRequested();

        if (account.ConnectorKind != ChannelConnectorKind.WeChat)
        {
            throw new ArgumentException(
                $"WeChat connector cannot start account with connector kind '{account.ConnectorKind}'.",
                nameof(account));
        }

        var accountId = ValidateAndNormalizeAccountId(account.Id);
        var configuration = await WeChatConnectorConfiguration
            .FromAccountAsync(account, _workspaceRootPath, _secretResolver, cancellationToken)
            .ConfigureAwait(false);

        // 设置 API Client 的 BotToken
        _apiClient.SetBotToken(configuration.BotToken);

        // 加载持久化游标
        var syncBuf = _authManager.LoadSyncBuf(configuration.StateDir);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var startedAccount = new StartedAccount(
            Account: account with { Id = accountId },
            Configuration: configuration,
            Cts: cts,
            SyncBuf: syncBuf);

        if (!_startedAccounts.TryAdd(accountId, startedAccount))
        {
            cts.Dispose();
            throw new InvalidOperationException($"WeChat account '{accountId}' is already started.");
        }

        // 启动后台长轮询，不等待
        _logger.LogInformation("WeChat connector starting poll loop for account {AccountId}", accountId);

        startedAccount.LoopTask = Task.Run(
            () => PollLoopAsync(startedAccount, onEvent, cts.Token),
            CancellationToken.None);

        RecordDiagnosticEvent("wechat.account_started", "info",
            $"WeChat account started: accountId={accountId}");
    }

    public async Task StopAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = ValidateAndNormalizeAccountId(accountId);

        if (!_startedAccounts.TryRemove(normalizedAccountId, out var startedAccount))
            return;

        await startedAccount.Cts.CancelAsync().ConfigureAwait(false);

        // 等待 Poll Loop 真正退出，防止新 StartAsync 与旧 Loop 并发运行处理相同消息
        try
        {
            await startedAccount.LoopTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("WeChat poll loop for account {AccountId} did not stop within 5s", accountId);
        }
        catch (Exception) { /* OperationCanceledException 或其他异常均可忽略 */ }

        startedAccount.Cts.Dispose();

        RecordDiagnosticEvent("wechat.account_stopped", "info",
            $"WeChat account stopped: accountId={accountId}");
    }

    public Task SendAsync(ChannelOutboundDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.ConnectorKind != ChannelConnectorKind.WeChat)
        {
            throw new ArgumentException(
                $"WeChat connector cannot send draft with connector kind '{draft.ConnectorKind}'.",
                nameof(draft));
        }

        var accountId = ValidateAndNormalizeAccountId(draft.AccountId);
        if (!_startedAccounts.TryGetValue(accountId, out var startedAccount))
        {
            throw new InvalidOperationException(
                $"WeChat account '{accountId}' must be started before outbound delivery.");
        }

        if (string.IsNullOrWhiteSpace(draft.ExternalThreadId))
            throw new ArgumentException("WeChat outbound draft must provide an external thread id.", nameof(draft));

        // 优先从内存缓存读取 contextToken
        var cacheKey = $"{accountId}::{draft.ExternalThreadId}";
        var contextToken = _contextTokenCache.TryGetValue(cacheKey, out var cached)
            ? cached
            : ReadContextToken(draft.MetadataJson);
        _logger.LogInformation(
            "WeChat SendAsync: accountId={AccountId} toUserId={ToUserId} contextTokenLen={Len}",
            accountId, draft.ExternalThreadId, contextToken.Length);

        return SendOutboundAsync(draft, startedAccount, contextToken, cancellationToken);
    }

    // ── 出站发送 ─────────────────────────────────────────────

    private async Task SendOutboundAsync(
        ChannelOutboundDraft draft,
        StartedAccount ctx,
        string contextToken,
        CancellationToken ct)
    {
        // 先发媒体附件（每个独立一条消息）
        if (draft.MediaAttachments is { Count: > 0 } && _mediaStore is not null)
        {
            foreach (var mediaRef in draft.MediaAttachments)
            {
                // 音频需 AMR 转码，降级为文件发送；视频走原生 video_item 路径
                var ilinkMediaType = ResolveILinkMediaType(mediaRef.ContentType);

                try
                {
                    await SendMediaAttachmentAsync(
                        draft.ExternalThreadId, contextToken, mediaRef, ilinkMediaType, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "WeChat: failed to send media attachment {MediaId}, skipping",
                        mediaRef.MediaId);
                }
            }
        }

        // 再发文字（如果有）
        if (!string.IsNullOrWhiteSpace(draft.MessageText))
        {
            var plainText = MarkdownToPlainText(draft.MessageText);
            await _apiClient.SendTextAsync(draft.ExternalThreadId, contextToken, plainText, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task SendMediaAttachmentAsync(
        string toUserId,
        string contextToken,
        MediaReference mediaRef,
        int ilinkMediaType,
        CancellationToken ct)
    {
        // 1. 读取 MediaStore
        var stream = await _mediaStore!.OpenReadAsync(mediaRef.MediaId, ct).ConfigureAwait(false);
        if (stream is null)
        {
            _logger.LogWarning("WeChat: MediaStore returned null for mediaId={MediaId}", mediaRef.MediaId);
            return;
        }

        using (stream)
        {
            var rawBytes = await ReadAllBytesAsync(stream, ct).ConfigureAwait(false);

            // 2. 生成 AES-128 key（图片用 raw bytes，文件用 hex string）
            var keyRaw = RandomNumberGenerator.GetBytes(16);
            var isImage = ilinkMediaType == ILinkMediaTypeImage;
            var aesKeyBase64 = isImage
                ? Convert.ToBase64String(keyRaw)
                : Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToHexString(keyRaw).ToLowerInvariant()));

            // 3. AES-ECB 加密
            var encrypted = HttpWeChatCdnClient.AesEcbEncrypt(rawBytes, keyRaw);

            // 4. 计算原始文件 MD5
            var rawMd5 = Convert.ToHexString(MD5.HashData(rawBytes)).ToLowerInvariant();

            // 5. getuploadurl
            var uploadResp = await _apiClient.GetUploadUrlAsync(new ILinkGetUploadUrlRequest
            {
                FileKey = Guid.NewGuid().ToString("N"),
                MediaType = ilinkMediaType,
                ToUserId = toUserId,
                RawSize = rawBytes.Length,
                RawFileMd5 = rawMd5,
                FileSize = encrypted.Length,
                AesKey = aesKeyBase64
            }, ct).ConfigureAwait(false);

            if (uploadResp.UploadParam is null)
                throw new InvalidOperationException("getuploadurl returned no UploadParam");

            // 6. 上传到 CDN
            var encryptQueryParam = await _cdnClient.UploadEncryptedAsync(
                uploadResp.UploadParam, encrypted, ct).ConfigureAwait(false);

            // 7. 构造 item_list 并发送
            var media = new ILinkMedia
            {
                EncryptQueryParam = encryptQueryParam,
                AesKey = aesKeyBase64,
                EncryptType = 1
            };

            ILinkMessageItem item = ilinkMediaType switch
            {
                ILinkMediaTypeImage => new ILinkMessageItem
                {
                    Type = ILinkMessageTypeImage,
                    ImageItem = new ILinkImageItem { Media = media }
                },
                ILinkMediaTypeVideo => new ILinkMessageItem
                {
                    Type = ILinkMessageTypeVideo,
                    VideoItem = new ILinkVideoItem
                    {
                        Media = media,
                        VideoSize = encrypted.Length
                    }
                },
                _ => new ILinkMessageItem
                {
                    Type = ILinkMessageTypeFile,
                    FileItem = new ILinkFileItem
                    {
                        Media = media,
                        FileName = mediaRef.FileName ?? "file",
                        Len = encrypted.Length.ToString()
                    }
                }
            };

            await _apiClient.SendMediaAsync(toUserId, contextToken, [item], ct).ConfigureAwait(false);
        }
    }

    private static int ResolveILinkMediaType(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return ILinkMediaTypeFile;

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return ILinkMediaTypeImage;

        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return ILinkMediaTypeVideo;

        return ILinkMediaTypeFile;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        if (stream is MemoryStream ms) return ms.ToArray();
        using var buf = new MemoryStream();
        await stream.CopyToAsync(buf, ct).ConfigureAwait(false);
        return buf.ToArray();
    }

    // ── 长轮询主循环 ──────────────────────────────────────────

    private async Task PollLoopAsync(
        StartedAccount ctx,
        Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken ct)
    {
        _logger.LogInformation("WeChat poll loop started for account {AccountId}", ctx.Account.Id);
        try
        {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var response = await _apiClient.GetUpdatesAsync(ctx.SyncBuf, ct).ConfigureAwait(false);

                if (response.Ret == ILinkErrCodeSessionExpired)
                {
                    _logger.LogWarning("WeChat session expired for account {AccountId}, marking degraded", ctx.Account.Id);
                    await MarkAccountDegradedAsync(ctx.Account, "iLink session expired (errcode -14). Re-login required.", ct)
                        .ConfigureAwait(false);
                    await Task.Delay(_options.SessionExpiredRetryDelayMs, ct).ConfigureAwait(false);
                    continue;
                }

                // 更新游标
                if (!string.IsNullOrEmpty(response.GetUpdatesBuf))
                {
                    ctx.SyncBuf = response.GetUpdatesBuf;
                    _authManager.SaveSyncBuf(ctx.Configuration.StateDir, ctx.SyncBuf);
                }

                if (response.Msgs.Count > 0)
                    _logger.LogInformation("WeChat received {Count} message(s) for account {AccountId}", response.Msgs.Count, ctx.Account.Id);

                foreach (var msg in response.Msgs)
                {
                    // LRU 去重
                    if (ctx.SeenMessageIds.Contains(msg.MessageId))
                        continue;
                    ctx.SeenMessageIds.Add(msg.MessageId);
                    if (ctx.SeenMessageIds.Count > _options.LruDeduplicationSize)
                        ctx.SeenMessageIds.RemoveAt(0);

                    // 缓存 contextToken，供后续回复时使用
                    if (!string.IsNullOrEmpty(msg.ContextToken))
                    {
                        var cacheKey = $"{ctx.Account.Id}::{msg.FromUserId}";
                        _contextTokenCache[cacheKey] = msg.ContextToken;
                    }

                    await ProcessMessageItemsAsync(ctx.Account, msg, ctx.Configuration, onEvent, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WeChat poll error for account {AccountId}, retrying in {DelayMs}ms", ctx.Account.Id, _options.ErrorRetryDelayMs);
                RecordDiagnosticEvent("wechat.polling_error", "warning",
                    $"WeChat polling error (retrying): accountId={ctx.Account.Id} error={ex.GetBaseException().Message}");
                try { await Task.Delay(_options.ErrorRetryDelayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        }
        finally
        {
            _logger.LogInformation("WeChat poll loop stopped for account {AccountId}", ctx.Account.Id);
        }
    }

    // ── 消息解析（入站）──────────────────────────────────────

    private async Task ProcessMessageItemsAsync(
        ChannelAccount account,
        ILinkMessage msg,
        WeChatConnectorConfiguration configuration,
        Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken ct)
    {
        string? text = null;
        List<MediaReference>? attachments = null;

        foreach (var item in msg.ItemList)
        {
            switch (item.Type)
            {
                case ILinkMessageTypeText:
                    if (!string.IsNullOrWhiteSpace(item.TextItem?.Text))
                        text = item.TextItem.Text;
                    break;

                case ILinkMessageTypeImage:
                    if (item.ImageItem?.Media is { } imgMedia && _mediaStore is not null)
                    {
                        var imgRef = await TryDownloadMediaAsync(
                            imgMedia, isImage: true, "image/jpeg", null, msg.MessageId, ct)
                            .ConfigureAwait(false);
                        if (imgRef is not null)
                            (attachments ??= []).Add(imgRef);
                    }
                    break;

                case ILinkMessageTypeFile:
                    if (item.FileItem?.Media is { } fileMedia && _mediaStore is not null)
                    {
                        var fileName = item.FileItem.FileName;
                        var contentType = InferContentType(fileName);
                        var fileRef = await TryDownloadMediaAsync(
                            fileMedia, isImage: false, contentType, fileName, msg.MessageId, ct)
                            .ConfigureAwait(false);
                        if (fileRef is not null)
                            (attachments ??= []).Add(fileRef);
                    }
                    break;
            }
        }

        // 有文字或媒体附件时才触发事件
        if (text is null && (attachments is null || attachments.Count == 0))
            return;

        var envelope = BuildEnvelopeWithMedia(account, msg, text, attachments, configuration);
        await SendTypingAndProcessAsync(msg.FromUserId, msg.ContextToken, envelope, onEvent, ct)
            .ConfigureAwait(false);
    }

    private async Task<MediaReference?> TryDownloadMediaAsync(
        ILinkMedia media,
        bool isImage,
        string contentType,
        string? fileName,
        long messageId,
        CancellationToken ct)
    {
        if (_mediaStore is null) return null;
        if (string.IsNullOrEmpty(media.EncryptQueryParam) || string.IsNullOrEmpty(media.AesKey))
            return null;

        try
        {
            var cdnBaseUrl = HttpWeChatApiClient.BaseUrl;
            var stream = await _cdnClient.DownloadAndDecryptAsync(
                cdnBaseUrl, media.EncryptQueryParam, media.AesKey, isImage, ct)
                .ConfigureAwait(false);

            var meta = await _mediaStore.StoreAsync(
                stream, contentType, fileName,
                source: "wechat",
                externalMessageId: messageId.ToString(),
                cancellationToken: ct)
                .ConfigureAwait(false);

            return new MediaReference(meta.Id, contentType, fileName, meta.SizeBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeChat: failed to download media for message {MessageId}", messageId);
            return null;
        }
    }

    private static string InferContentType(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "application/octet-stream";
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            ".zip" => "application/zip",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }

    // ── 正在输入 ──────────────────────────────────────────────

    private async Task SendTypingAndProcessAsync(
        string fromUserId,
        string contextToken,
        ChannelEventEnvelope envelope,
        Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken ct)
    {
        // 尝试获取 typing_ticket；失败时直接处理消息（typing 是可选特性）
        string? typingTicket = null;
        if (!string.IsNullOrEmpty(contextToken))
        {
            try
            {
                var config = await _apiClient.GetConfigAsync(fromUserId, contextToken, ct).ConfigureAwait(false);
                if (config.Ret == 0 && !string.IsNullOrEmpty(config.TypingTicket))
                    typingTicket = config.TypingTicket;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WeChat getconfig failed for {UserId}, skipping typing indicator", fromUserId);
            }
        }

        if (typingTicket is null)
        {
            await onEvent(envelope, ct).ConfigureAwait(false);
            return;
        }

        // 发送"正在输入"并在 Agent 处理期间每 5 秒续发一次
        try { await _apiClient.SendTypingAsync(fromUserId, typingTicket, 1, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "WeChat sendtyping(1) failed"); }

        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var typingLoop = Task.Run(async () =>
        {
            while (!typingCts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(5_000, typingCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try { await _apiClient.SendTypingAsync(fromUserId, typingTicket, 1, typingCts.Token).ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }, CancellationToken.None);

        try
        {
            await onEvent(envelope, ct).ConfigureAwait(false);
        }
        finally
        {
            await typingCts.CancelAsync().ConfigureAwait(false);
            try { await typingLoop.ConfigureAwait(false); } catch { }
            // 取消输入中状态（用 None，不受 ct 影响）
            try { await _apiClient.SendTypingAsync(fromUserId, typingTicket, 2, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "WeChat sendtyping(2) failed"); }
        }
    }

    // ── 事件构造 ─────────────────────────────────────────────

    private static ChannelEventEnvelope BuildEnvelopeWithMedia(
        ChannelAccount account,
        ILinkMessage msg,
        string? text,
        IReadOnlyList<MediaReference>? mediaAttachments,
        WeChatConnectorConfiguration configuration)
    {
        var metadataJson = JsonSerializer.Serialize(new { contextToken = msg.ContextToken });

        return new ChannelEventEnvelope(
            EventId: $"wechat-{msg.MessageId}",
            EventType: ChannelEventType.MessageReceived,
            ConnectorKind: ChannelConnectorKind.WeChat,
            AccountId: account.Id,
            ExternalThreadId: msg.FromUserId,
            ThreadType: ChannelThreadType.DirectMessage,
            OccurredAt: DateTimeOffset.UtcNow,
            Sender: new ChannelIdentity(msg.FromUserId, null, msg.FromUserId),
            Recipient: null,
            ExternalMessageId: msg.MessageId.ToString(),
            Text: text,
            MetadataJson: metadataJson,
            DefaultDeliveryMode: configuration.DefaultDeliveryMode,
            MediaAttachments: mediaAttachments);
    }

    // ── 账号状态管理 ─────────────────────────────────────────

    private async Task MarkAccountDegradedAsync(ChannelAccount account, string error, CancellationToken ct)
    {
        if (_accountRepository is null) return;

        var degraded = account with
        {
            State = ChannelAccountState.Degraded,
            LastError = error
        };

        await _accountRepository.UpsertAsync(degraded, ct).ConfigureAwait(false);
    }

    // ── 工具方法 ─────────────────────────────────────────────

    private static string ReadContextToken(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("contextToken", out var prop)
                && prop.ValueKind == JsonValueKind.String)
                return prop.GetString() ?? string.Empty;
        }
        catch { /* 忽略 */ }

        return string.Empty;
    }

    internal static string MarkdownToPlainText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 替换匹配到的 Markdown 标记：保留文字内容，去掉标记符号
        var result = MarkdownRegex.Replace(text, match =>
        {
            // 加粗/斜体：保留内容组
            if (match.Groups[2].Success) return match.Groups[2].Value;
            if (match.Groups[4].Success) return match.Groups[4].Value;
            // 链接：保留链接文字
            if (match.Groups[5].Success) return match.Groups[5].Value;
            // 标题/引用/代码块：去掉标记，保留空字符串（后续 trim）
            return string.Empty;
        });

        return result.Trim();
    }

    private void RecordDiagnosticEvent(string eventType, string level, string message)
    {
        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: DiagnosticSource,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow));
    }

    private static string ValidateAndNormalizeAccountId(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("Account ID must not be empty.", nameof(accountId));
        return accountId.Trim();
    }

    // ── 内部状态 ─────────────────────────────────────────────

    private sealed class StartedAccount(
        ChannelAccount Account,
        WeChatConnectorConfiguration Configuration,
        CancellationTokenSource Cts,
        string SyncBuf)
    {
        public ChannelAccount Account { get; } = Account;
        public WeChatConnectorConfiguration Configuration { get; } = Configuration;
        public CancellationTokenSource Cts { get; } = Cts;
        public string SyncBuf { get; set; } = SyncBuf;

        /// <summary>后台轮询 Task，供 StopAsync 等待退出</summary>
        public Task LoopTask { get; set; } = Task.CompletedTask;

        /// <summary>内存 LRU 消息 ID 列表（有序，最早加入的在前）</summary>
        public List<long> SeenMessageIds { get; } = new();
    }
}
