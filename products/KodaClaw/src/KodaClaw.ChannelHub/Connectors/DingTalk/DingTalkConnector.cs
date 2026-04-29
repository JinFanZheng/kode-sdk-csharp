using System.Collections.Concurrent;
using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Media;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Media;
using Microsoft.Extensions.Logging;

namespace KodaClaw.ChannelHub.Connectors.DingTalk;

public sealed class DingTalkConnector : IChannelConnector
{
    private const string DiagnosticSource = "dingtalk";

    private readonly ConcurrentDictionary<string, StartedAccount> _startedAccounts =
        new(StringComparer.Ordinal);

    // conversationId → senderStaffId（用于单聊回复时知道对方的 userId）
    private readonly ConcurrentDictionary<string, string> _conversationUserCache =
        new(StringComparer.Ordinal);

    // conversationId → (webhookUrl, expiredAt)（用于群聊优先路径）
    private readonly ConcurrentDictionary<string, (string Url, DateTimeOffset ExpiredAt)>
        _conversationWebhookCache = new(StringComparer.Ordinal);

    private readonly IDingTalkApiClient _apiClient;
    private readonly DingTalkConnectorOptions _options;
    private readonly ChannelSecretResolver _secretResolver;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly IMediaStore? _mediaStore;
    private readonly ILogger<DingTalkConnector> _logger;

    public DingTalkConnector(
        ILogger<DingTalkConnector> logger,
        IDingTalkApiClient? apiClient = null,
        DingTalkConnectorOptions? options = null,
        ISecretStore? secretStore = null,
        IDiagnosticsService? diagnosticsService = null,
        IMediaStore? mediaStore = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiClient = apiClient ?? new HttpDingTalkApiClient();
        _options = options ?? new DingTalkConnectorOptions();
        _secretResolver = new ChannelSecretResolver(secretStore);
        _diagnosticsService = diagnosticsService;
        _mediaStore = mediaStore;
    }

    public ChannelConnectorKind Kind => ChannelConnectorKind.DingTalk;

    public async Task StartAsync(
        ChannelAccount account,
        Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(onEvent);
        cancellationToken.ThrowIfCancellationRequested();

        if (account.ConnectorKind != ChannelConnectorKind.DingTalk)
        {
            throw new ArgumentException(
                $"DingTalk connector cannot start account with connector kind '{account.ConnectorKind}'.",
                nameof(account));
        }

        var accountId = ValidateAndNormalizeAccountId(account.Id);
        var configuration = await DingTalkConnectorConfiguration
            .FromAccountAsync(account, _secretResolver, cancellationToken)
            .ConfigureAwait(false);

        var streamClient = new DingTalkStreamClient(
            _apiClient,
            configuration.AppKey,
            configuration.AppSecret,
            (eventData, messageId, ct) => DispatchEventAsync(accountId, account, configuration, eventData, messageId, onEvent, ct),
            _options,
            _diagnosticsService);

        var startedAccount = new StartedAccount(
            account: account with { Id = accountId },
            configuration: configuration,
            streamClient: streamClient);

        if (!_startedAccounts.TryAdd(accountId, startedAccount))
        {
            _ = streamClient.DisposeAsync();
            throw new InvalidOperationException($"DingTalk account '{accountId}' is already started.");
        }

        // Fire-and-forget：流连接在后台建立
        streamClient.Start(cancellationToken);

        _logger.LogInformation("DingTalk Stream connector starting for account {AccountId}", accountId);
        RecordDiagnosticEvent("dingtalk.account_started", "info",
            $"DingTalk account started: accountId={accountId}");
    }

    public async Task StopAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = ValidateAndNormalizeAccountId(accountId);

        if (!_startedAccounts.TryRemove(normalizedAccountId, out var startedAccount))
        {
            return;
        }

        await startedAccount.StreamClient.StopAsync().ConfigureAwait(false);
        await startedAccount.StreamClient.DisposeAsync().ConfigureAwait(false);

        RecordDiagnosticEvent("dingtalk.account_stopped", "info",
            $"DingTalk account stopped: accountId={accountId}");
    }

    public async Task SendAsync(ChannelOutboundDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.ConnectorKind != ChannelConnectorKind.DingTalk)
        {
            throw new ArgumentException(
                $"DingTalk connector cannot send draft with connector kind '{draft.ConnectorKind}'.",
                nameof(draft));
        }

        var accountId = ValidateAndNormalizeAccountId(draft.AccountId);
        if (!_startedAccounts.TryGetValue(accountId, out var startedAccount))
        {
            throw new InvalidOperationException(
                $"DingTalk account '{accountId}' must be started before outbound delivery.");
        }

        if (string.IsNullOrWhiteSpace(draft.ExternalThreadId))
        {
            throw new ArgumentException("DingTalk outbound draft must provide an external thread id.", nameof(draft));
        }

        // ExternalThreadId 格式：conversationId:{id}
        var conversationId = ParseConversationId(draft.ExternalThreadId);

        // 媒体附件出站：上传 → 构造 msgKey/msgParam → 发送
        if (draft.MediaAttachments is { Count: > 0 } && _mediaStore is not null)
        {
            var attachment = draft.MediaAttachments[0];
            var fallbackText = draft.MessageText;

            try
            {
                var meta = await _mediaStore.GetMetaAsync(attachment.MediaId, cancellationToken).ConfigureAwait(false);
                if (meta is null)
                {
                    _logger.LogWarning("Media {MediaId} not found in store, falling back to text", attachment.MediaId);
                }
                else
                {
                    await using var stream = await _mediaStore.OpenReadAsync(attachment.MediaId, cancellationToken).ConfigureAwait(false);
                    if (stream is not null)
                    {
                        var accessToken = await _apiClient.GetAccessTokenAsync(
                            startedAccount.Configuration.AppKey,
                            startedAccount.Configuration.AppSecret,
                            cancellationToken).ConfigureAwait(false);

                        var dingMediaId = await _apiClient.UploadMediaAsync(
                            accessToken,
                            startedAccount.Configuration.RobotCode,
                            stream,
                            attachment.ContentType,
                            meta.FileName,
                            cancellationToken).ConfigureAwait(false);

                        // Ensure duration is available for audio messages
                        var audioAttachment = attachment;
                        if (attachment.DurationMs is null && meta.DurationMs.HasValue)
                        {
                            audioAttachment = attachment with { DurationMs = meta.DurationMs.Value };
                        }
                        var (msgKey, msgParam) = BuildMediaMsgKeyAndParam(audioAttachment, dingMediaId);

                        if (draft.ThreadType == ChannelThreadType.Group)
                        {
                            await SendMediaGroupMessageAsync(
                                startedAccount, conversationId, msgKey, msgParam, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await SendMediaDirectMessageAsync(
                                startedAccount, conversationId, msgKey, msgParam, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send media {MediaId}, falling back to text", attachment.MediaId);
            }

            // fallback：发送纯文本
            if (!string.IsNullOrWhiteSpace(fallbackText))
            {
                if (draft.ThreadType == ChannelThreadType.Group)
                {
                    await SendGroupMessageInternalAsync(startedAccount, conversationId, fallbackText, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendDirectMessageAsync(startedAccount, conversationId, fallbackText, draft.MetadataJson, draft.Format, cancellationToken).ConfigureAwait(false);
                }
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.MessageText))
        {
            throw new ArgumentException(
                "DingTalk outbound draft message text is required.", nameof(draft));
        }

        if (draft.ThreadType == ChannelThreadType.Group)
        {
            await SendGroupMessageInternalAsync(
                startedAccount, conversationId, draft.MessageText, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await SendDirectMessageAsync(
                startedAccount, conversationId, draft.MessageText, draft.MetadataJson, draft.Format, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task SendMediaDirectMessageAsync(
        StartedAccount startedAccount,
        string conversationId,
        string msgKey,
        string msgParam,
        CancellationToken cancellationToken)
    {
        if (!_conversationUserCache.TryGetValue(conversationId, out var recipientUserId)
            || string.IsNullOrWhiteSpace(recipientUserId))
        {
            throw new InvalidOperationException(
                $"DingTalk cannot send media to conversation \'{conversationId}\': recipient userId not cached.");
        }

        var accessToken = await _apiClient.GetAccessTokenAsync(
            startedAccount.Configuration.AppKey,
            startedAccount.Configuration.AppSecret,
            cancellationToken).ConfigureAwait(false);

        string[] userIds = [recipientUserId];
        await _apiClient.SendMediaBatchAsync(
            accessToken,
            startedAccount.Configuration.RobotCode,
            userIds,
            msgKey,
            msgParam,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMediaGroupMessageAsync(
        StartedAccount startedAccount,
        string conversationId,
        string msgKey,
        string msgParam,
        CancellationToken cancellationToken)
    {
        if (_conversationWebhookCache.TryGetValue(conversationId, out var webhookEntry)
            && webhookEntry.ExpiredAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            await _apiClient.SendSessionWebhookMessageAsync(
                webhookEntry.Url, msgKey, msgParam, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var accessToken = await _apiClient.GetAccessTokenAsync(
            startedAccount.Configuration.AppKey,
            startedAccount.Configuration.AppSecret,
            cancellationToken).ConfigureAwait(false);

        await _apiClient.SendGroupMessageAsync(
            accessToken,
            startedAccount.Configuration.RobotCode,
            conversationId,
            msgKey,
            msgParam,
            cancellationToken).ConfigureAwait(false);
    }

    private static (string MsgKey, string MsgParam) BuildMediaMsgKeyAndParam(
        MediaReference attachment, string dingMediaId)
    {
        var ct = attachment.ContentType ?? string.Empty;

        if (ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            var msgParam = JsonSerializer.Serialize(new { photoId = dingMediaId });
            return ("sampleImage", msgParam);
        }

        if (ct.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            var durationSec = attachment.DurationMs.HasValue
                ? (int)Math.Ceiling(attachment.DurationMs.Value / 1000.0)
                : 0;
            var msgParam = JsonSerializer.Serialize(new { audioId = dingMediaId, duration = durationSec });
            return ("sampleAudio", msgParam);
        }

        if (ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            var durationSec = attachment.DurationMs.HasValue
                ? (int)Math.Ceiling(attachment.DurationMs.Value / 1000.0)
                : 0;
            var msgParam = JsonSerializer.Serialize(new { videoId = dingMediaId, duration = durationSec });
            return ("sampleVideo", msgParam);
        }

        // 文件或其他类型
        var fileParam = JsonSerializer.Serialize(new
        {
            fileId = dingMediaId,
            fileName = attachment.FileName ?? "file"
        });
        return ("sampleFile", fileParam);
    }

    private async Task SendDirectMessageAsync(
        StartedAccount startedAccount,
        string conversationId,
        string messageText,
        string? metadataJson,
        OutboundMessageFormat format,
        CancellationToken cancellationToken)
    {
        if (!_conversationUserCache.TryGetValue(conversationId, out var recipientUserId)
            || string.IsNullOrWhiteSpace(recipientUserId))
        {
            throw new InvalidOperationException(
                $"DingTalk cannot send to conversation '{conversationId}': recipient userId not cached. " +
                "Wait for the user to send a message first.");
        }

        var accessToken = await _apiClient.GetAccessTokenAsync(
            startedAccount.Configuration.AppKey,
            startedAccount.Configuration.AppSecret,
            cancellationToken).ConfigureAwait(false);

        var robotCode = startedAccount.Configuration.RobotCode;
        string[] userIds = [recipientUserId];

        // ActionCard 整体跳转
        if (TryParseActionCardSingle(metadataJson, out var acTitle, out var acText, out var singleTitle, out var singleUrl))
        {
            await _apiClient.SendActionCardMessageAsync(
                accessToken, robotCode, userIds, acTitle, acText, singleTitle, singleUrl, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // ActionCard 独立多按钮
        if (TryParseActionCard6(metadataJson, out var ac6Title, out var ac6Text, out var btns))
        {
            await _apiClient.SendActionCard6MessageAsync(
                accessToken, robotCode, userIds, ac6Title, ac6Text, btns, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Format 决定是否使用 Markdown
        var useMarkdown = format switch
        {
            OutboundMessageFormat.Markdown => true,
            OutboundMessageFormat.PlainText => false,
            _ => ContainsMarkdown(messageText), // Auto：启发式检测
        };

        if (useMarkdown)
        {
            await _apiClient.SendMarkdownMessageAsync(
                accessToken, robotCode, userIds, "通知", messageText, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await _apiClient.SendTextMessageAsync(
            accessToken, robotCode, userIds, messageText, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendGroupMessageInternalAsync(
        StartedAccount startedAccount,
        string conversationId,
        string messageText,
        CancellationToken cancellationToken)
    {
        var msgKey = "sampleText";
        var msgParam = System.Text.Json.JsonSerializer.Serialize(new { content = messageText });

        // 优先使用 sessionWebhook（有效期内，留 1 分钟余量）
        if (_conversationWebhookCache.TryGetValue(conversationId, out var webhookEntry)
            && webhookEntry.ExpiredAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            await _apiClient.SendSessionWebhookMessageAsync(
                webhookEntry.Url, msgKey, msgParam, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // fallback：orgGroupSend
        var accessToken = await _apiClient.GetAccessTokenAsync(
            startedAccount.Configuration.AppKey,
            startedAccount.Configuration.AppSecret,
            cancellationToken).ConfigureAwait(false);

        await _apiClient.SendGroupMessageAsync(
            accessToken,
            startedAccount.Configuration.RobotCode,
            conversationId,
            msgKey,
            msgParam,
            cancellationToken).ConfigureAwait(false);
    }

    // ── 事件映射 ──────────────────────────────────────────────────────────

    private async Task DispatchEventAsync(
        string accountId,
        ChannelAccount account,
        DingTalkConnectorConfiguration configuration,
        DingTalkStreamEventData eventData,
        string messageId,
        Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken)
    {
        var conversationId = eventData.ConversationId;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        // 缓存 conversationId → senderStaffId（优先 senderStaffId，降级 senderId）
        var senderUserId = eventData.SenderStaffId ?? eventData.SenderId;
        if (!string.IsNullOrWhiteSpace(senderUserId))
        {
            _conversationUserCache[conversationId] = senderUserId;
        }

        // 缓存群聊 sessionWebhook（每次收到消息都刷新，有效期约 2 小时）
        if (!string.IsNullOrWhiteSpace(eventData.SessionWebhook)
            && eventData.SessionWebhookExpiredTime.HasValue)
        {
            var expiredAt = DateTimeOffset.FromUnixTimeMilliseconds(
                eventData.SessionWebhookExpiredTime.Value);
            _conversationWebhookCache[conversationId] = (eventData.SessionWebhook, expiredAt);
        }

        ChannelEventEnvelope? channelEnvelope;

        var msgType = eventData.MsgType ?? string.Empty;
        if (string.Equals(msgType, "text", StringComparison.OrdinalIgnoreCase))
        {
            channelEnvelope = TryMapToEnvelope(accountId, configuration, eventData, messageId);
        }
        else if (string.Equals(msgType, "picture", StringComparison.OrdinalIgnoreCase))
        {
            channelEnvelope = await TryMapPictureEnvelopeAsync(
                accountId, configuration, eventData, messageId, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (string.Equals(msgType, "audio", StringComparison.OrdinalIgnoreCase))
        {
            channelEnvelope = await TryMapAudioEnvelopeAsync(
                accountId, configuration, eventData, messageId, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (string.Equals(msgType, "video", StringComparison.OrdinalIgnoreCase))
        {
            channelEnvelope = await TryMapVideoEnvelopeAsync(
                accountId, configuration, eventData, messageId, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (string.Equals(msgType, "file", StringComparison.OrdinalIgnoreCase))
        {
            channelEnvelope = await TryMapFileEnvelopeAsync(
                accountId, configuration, eventData, messageId, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            return;
        }

        if (channelEnvelope is null)
        {
            return;
        }

        await onEvent(channelEnvelope, cancellationToken).ConfigureAwait(false);
    }

    private static ChannelEventEnvelope? TryMapToEnvelope(
        string accountId,
        DingTalkConnectorConfiguration configuration,
        DingTalkStreamEventData eventData,
        string messageId)
    {
        var rawText = eventData.Text?.Content;
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        // 钉钉文本内容前面可能有 @ 相关的空格前缀，trim 处理
        var cleanText = rawText.Trim();
        if (string.IsNullOrEmpty(cleanText))
        {
            return null;
        }

        var conversationId = eventData.ConversationId;
        var externalThreadId = $"conversationId:{conversationId}";

        var threadType = ResolveThreadType(eventData.ConversationType);

        var occurredAt = eventData.CreateAt.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(eventData.CreateAt.Value)
            : DateTimeOffset.UtcNow;

        var senderUserId = eventData.SenderStaffId ?? eventData.SenderId ?? string.Empty;

        return new ChannelEventEnvelope(
            EventId: $"dingtalk-{messageId}",
            EventType: ChannelEventType.MessageReceived,
            ConnectorKind: ChannelConnectorKind.DingTalk,
            AccountId: accountId,
            ExternalThreadId: externalThreadId,
            ThreadType: threadType,
            OccurredAt: occurredAt,
            Sender: new ChannelIdentity(Id: senderUserId),
            Recipient: null,
            ExternalMessageId: eventData.MsgId,
            Text: cleanText,
            DefaultDeliveryMode: configuration.DefaultDeliveryMode);
    }

    private static ChannelThreadType ResolveThreadType(string? conversationType)
    {
        // 1 = 单聊, 2 = 群聊
        return string.Equals(conversationType, "1", StringComparison.Ordinal)
            ? ChannelThreadType.DirectMessage
            : ChannelThreadType.Group;
    }

    private static string ParseConversationId(string externalThreadId)
    {
        // 格式：conversationId:{id}
        const string prefix = "conversationId:";
        if (externalThreadId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return externalThreadId[prefix.Length..];
        }

        return externalThreadId;
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
        {
            throw new ArgumentException("A non-empty account id is required.", nameof(accountId));
        }

        return accountId.Trim();
    }

    private static bool ContainsMarkdown(string text)
        => text.Contains("##", StringComparison.Ordinal)
        || text.Contains("**", StringComparison.Ordinal);

    private static bool TryParseActionCardSingle(
        string? metadataJson,
        out string title, out string text, out string singleTitle, out string singleUrl)
    {
        title = text = singleTitle = singleUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(metadataJson))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("msgKey", out var msgKeyEl)
                || !string.Equals(msgKeyEl.GetString(), "sampleActionCard", StringComparison.Ordinal))
                return false;
            if (!root.TryGetProperty("title", out var t)
                || !root.TryGetProperty("text", out var tx)
                || !root.TryGetProperty("singleTitle", out var st)
                || !root.TryGetProperty("singleURL", out var su))
                return false;
            title = t.GetString() ?? string.Empty;
            text = tx.GetString() ?? string.Empty;
            singleTitle = st.GetString() ?? string.Empty;
            singleUrl = su.GetString() ?? string.Empty;
            return !string.IsNullOrEmpty(title);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseActionCard6(
        string? metadataJson,
        out string title, out string text, out IReadOnlyList<DingTalkActionCardBtn> btns)
    {
        title = text = string.Empty;
        btns = [];
        if (string.IsNullOrWhiteSpace(metadataJson))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("msgKey", out var msgKeyEl)
                || !string.Equals(msgKeyEl.GetString(), "sampleActionCard6", StringComparison.Ordinal))
                return false;
            if (!root.TryGetProperty("title", out var t)
                || !root.TryGetProperty("text", out var tx)
                || !root.TryGetProperty("btns", out var btnsEl))
                return false;
            title = t.GetString() ?? string.Empty;
            text = tx.GetString() ?? string.Empty;
            var list = new List<DingTalkActionCardBtn>();
            foreach (var btn in btnsEl.EnumerateArray())
            {
                var btnTitle = btn.TryGetProperty("title", out var bt) ? bt.GetString() ?? "" : "";
                var btnUrl = btn.TryGetProperty("actionURL", out var bu) ? bu.GetString() ?? "" : "";
                list.Add(new DingTalkActionCardBtn(btnTitle, btnUrl));
            }
            btns = list;
            return !string.IsNullOrEmpty(title) && list.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    // ── 多媒体入站处理 ────────────────────────────────────────────────────────

    private async Task<ChannelEventEnvelope?> TryMapPictureEnvelopeAsync(
        string accountId,
        DingTalkConnectorConfiguration configuration,
        DingTalkStreamEventData eventData,
        string messageId,
        CancellationToken cancellationToken)
    {
        if (_mediaStore is null) return null;

        var downloadCode = ParseDownloadCode(eventData.Content);
        if (string.IsNullOrWhiteSpace(downloadCode)) return null;

        try
        {
            var accessToken = await _apiClient.GetAccessTokenAsync(
                configuration.AppKey, configuration.AppSecret, cancellationToken)
                .ConfigureAwait(false);

            var downloadUrl = await _apiClient.GetMediaDownloadUrlAsync(
                accessToken, configuration.RobotCode, downloadCode, cancellationToken)
                .ConfigureAwait(false);

            using var stream = await _apiClient.DownloadMediaAsync(downloadUrl, cancellationToken)
                .ConfigureAwait(false);

            var storedMeta = await _mediaStore.StoreAsync(
                stream,
                contentType: "image/jpeg",
                fileName: $"dingtalk-image-{messageId}.jpg",
                source: "dingtalk-inbound",
                externalMessageId: eventData.MsgId,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return BuildMediaEnvelope(accountId, configuration, eventData, messageId,
                text: "[图片]",
                storedMeta: storedMeta);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to process inbound picture for message {MessageId}: {Message}",
                messageId, ex.Message);
            return null;
        }
    }

    private async Task<ChannelEventEnvelope?> TryMapAudioEnvelopeAsync(
        string accountId,
        DingTalkConnectorConfiguration configuration,
        DingTalkStreamEventData eventData,
        string messageId,
        CancellationToken cancellationToken)
    {
        if (_mediaStore is null) return null;

        var downloadCode = ParseDownloadCode(eventData.Content);
        if (string.IsNullOrWhiteSpace(downloadCode)) return null;

        try
        {
            var accessToken = await _apiClient.GetAccessTokenAsync(
                configuration.AppKey, configuration.AppSecret, cancellationToken)
                .ConfigureAwait(false);

            var downloadUrl = await _apiClient.GetMediaDownloadUrlAsync(
                accessToken, configuration.RobotCode, downloadCode, cancellationToken)
                .ConfigureAwait(false);

            using var stream = await _apiClient.DownloadMediaAsync(downloadUrl, cancellationToken)
                .ConfigureAwait(false);

            var storedMeta = await _mediaStore.StoreAsync(
                stream,
                contentType: "audio/amr",
                fileName: $"dingtalk-audio-{messageId}.amr",
                source: "dingtalk-inbound",
                externalMessageId: eventData.MsgId,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var recognition = ParseAudioRecognition(eventData.Content);
            var text = string.IsNullOrWhiteSpace(recognition) ? "[音频]" : $"[音频] {recognition}";

            return BuildMediaEnvelope(accountId, configuration, eventData, messageId,
                text: text,
                storedMeta: storedMeta);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to process inbound audio for message {MessageId}: {Message}",
                messageId, ex.Message);
            return null;
        }
    }

    private async Task<ChannelEventEnvelope?> TryMapVideoEnvelopeAsync(
        string accountId,
        DingTalkConnectorConfiguration configuration,
        DingTalkStreamEventData eventData,
        string messageId,
        CancellationToken cancellationToken)
    {
        if (_mediaStore is null) return null;

        var downloadCode = ParseDownloadCode(eventData.Content);
        if (string.IsNullOrWhiteSpace(downloadCode)) return null;

        try
        {
            var accessToken = await _apiClient.GetAccessTokenAsync(
                configuration.AppKey, configuration.AppSecret, cancellationToken)
                .ConfigureAwait(false);

            var downloadUrl = await _apiClient.GetMediaDownloadUrlAsync(
                accessToken, configuration.RobotCode, downloadCode, cancellationToken)
                .ConfigureAwait(false);

            using var stream = await _apiClient.DownloadMediaAsync(downloadUrl, cancellationToken)
                .ConfigureAwait(false);

            var storedMeta = await _mediaStore.StoreAsync(
                stream,
                contentType: "video/mp4",
                fileName: $"dingtalk-video-{messageId}.mp4",
                source: "dingtalk-inbound",
                externalMessageId: eventData.MsgId,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return BuildMediaEnvelope(accountId, configuration, eventData, messageId,
                text: "[视频]",
                storedMeta: storedMeta);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to process inbound video for message {MessageId}: {Message}",
                messageId, ex.Message);
            return null;
        }
    }

    private async Task<ChannelEventEnvelope?> TryMapFileEnvelopeAsync(
        string accountId,
        DingTalkConnectorConfiguration configuration,
        DingTalkStreamEventData eventData,
        string messageId,
        CancellationToken cancellationToken)
    {
        if (_mediaStore is null) return null;

        var downloadCode = ParseDownloadCode(eventData.Content);
        if (string.IsNullOrWhiteSpace(downloadCode)) return null;

        var fileName = ParseFileName(eventData.Content) ?? $"dingtalk-file-{messageId}";

        try
        {
            var accessToken = await _apiClient.GetAccessTokenAsync(
                configuration.AppKey, configuration.AppSecret, cancellationToken)
                .ConfigureAwait(false);

            var downloadUrl = await _apiClient.GetMediaDownloadUrlAsync(
                accessToken, configuration.RobotCode, downloadCode, cancellationToken)
                .ConfigureAwait(false);

            using var stream = await _apiClient.DownloadMediaAsync(downloadUrl, cancellationToken)
                .ConfigureAwait(false);

            var contentType = InferContentTypeFromFileName(fileName);
            var storedMeta = await _mediaStore.StoreAsync(
                stream,
                contentType: contentType,
                fileName: fileName,
                source: "dingtalk-inbound",
                externalMessageId: eventData.MsgId,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return BuildMediaEnvelope(accountId, configuration, eventData, messageId,
                text: $"[文件: {fileName}]",
                storedMeta: storedMeta);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to process inbound file for message {MessageId}: {Message}",
                messageId, ex.Message);
            return null;
        }
    }

    private static ChannelEventEnvelope BuildMediaEnvelope(
        string accountId,
        DingTalkConnectorConfiguration configuration,
        DingTalkStreamEventData eventData,
        string messageId,
        string text,
        MediaMeta storedMeta)
    {
        var conversationId = eventData.ConversationId;
        var externalThreadId = $"conversationId:{conversationId}";
        var threadType = ResolveThreadType(eventData.ConversationType);
        var occurredAt = eventData.CreateAt.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(eventData.CreateAt.Value)
            : DateTimeOffset.UtcNow;
        var senderUserId = eventData.SenderStaffId ?? eventData.SenderId ?? string.Empty;

        return new ChannelEventEnvelope(
            EventId: $"dingtalk-{messageId}",
            EventType: ChannelEventType.MessageReceived,
            ConnectorKind: ChannelConnectorKind.DingTalk,
            AccountId: accountId,
            ExternalThreadId: externalThreadId,
            ThreadType: threadType,
            OccurredAt: occurredAt,
            Sender: new ChannelIdentity(Id: senderUserId),
            Recipient: null,
            ExternalMessageId: eventData.MsgId,
            Text: text,
            DefaultDeliveryMode: configuration.DefaultDeliveryMode,
            MediaAttachments: [
                new MediaReference(
                    MediaId: storedMeta.Id,
                    ContentType: storedMeta.ContentType,
                    FileName: storedMeta.FileName,
                    SizeBytes: storedMeta.SizeBytes)
            ]);
    }

    // ── Content JSON 解析辅助 ─────────────────────────────────────────────

    private static string? ParseDownloadCode(JsonElement? contentJson)
    {
        if (contentJson is null) return null;
        return contentJson.Value.TryGetProperty("downloadCode", out var el)
            ? el.GetString()
            : null;
    }

    private static string? ParseAudioRecognition(JsonElement? contentJson)
    {
        if (contentJson is null) return null;
        return contentJson.Value.TryGetProperty("recognition", out var el)
            ? el.GetString()
            : null;
    }

    private static string? ParseFileName(JsonElement? contentJson)
    {
        if (contentJson is null) return null;
        return contentJson.Value.TryGetProperty("fileName", out var el)
            ? el.GetString()
            : null;
    }

    private static string InferContentTypeFromFileName(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png"           => "image/png",
            "gif"           => "image/gif",
            "bmp"           => "image/bmp",
            "webp"          => "image/webp",
            "mp3"           => "audio/mpeg",
            "wav"           => "audio/wav",
            "amr"           => "audio/amr",
            "ogg"           => "audio/ogg",
            "m4a"           => "audio/mp4",
            "flac"          => "audio/flac",
            "mp4"           => "video/mp4",
            "mov"           => "video/quicktime",
            "avi"           => "video/x-msvideo",
            "mkv"           => "video/x-matroska",
            "pdf"           => "application/pdf",
            "doc" or "docx" => "application/msword",
            "xls" or "xlsx" => "application/vnd.ms-excel",
            "ppt" or "pptx" => "application/vnd.ms-powerpoint",
            _               => "application/octet-stream",
        };
    }

    // ── 测试辅助（internal，供单元测试直接注入缓存状态）────────────────────────



    internal void SetConversationUserCache(string conversationId, string userId)
        => _conversationUserCache[conversationId] = userId;

    internal void SetConversationWebhookCache(
        string conversationId, string webhookUrl, DateTimeOffset expiredAt)
        => _conversationWebhookCache[conversationId] = (webhookUrl, expiredAt);

    private sealed class StartedAccount
    {
        public StartedAccount(
            ChannelAccount account,
            DingTalkConnectorConfiguration configuration,
            DingTalkStreamClient streamClient)
        {
            Account = account;
            Configuration = configuration;
            StreamClient = streamClient;
        }

        public ChannelAccount Account { get; }
        public DingTalkConnectorConfiguration Configuration { get; }
        public DingTalkStreamClient StreamClient { get; }
    }
}
