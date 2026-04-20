using System.Collections.Concurrent;
using System.Globalization;
using KodaClaw.Contracts;
using KodaClaw.Workspace;

namespace KodaClaw.ChannelHub.Connectors.Telegram;

public sealed class TelegramConnector : IChannelConnector
{
    private const string DiagnosticSource = "telegram";

    private readonly ConcurrentDictionary<string, StartedAccount> _startedAccounts = new(StringComparer.Ordinal);
    private readonly ITelegramApiClient _apiClient;
    private readonly TelegramConnectorOptions _options;
    private readonly ChannelSecretResolver _secretResolver;
    private readonly IMediaStore? _mediaStore;
    private readonly IDiagnosticsService? _diagnosticsService;

    public TelegramConnector(
        ITelegramApiClient? apiClient = null,
        TelegramConnectorOptions? options = null,
        ISecretStore? secretStore = null,
        IMediaStore? mediaStore = null,
        IDiagnosticsService? diagnosticsService = null)
    {
        _apiClient = apiClient ?? new HttpTelegramApiClient();
        _options = options ?? new TelegramConnectorOptions();
        _secretResolver = new ChannelSecretResolver(secretStore);
        _mediaStore = mediaStore;
        _diagnosticsService = diagnosticsService;
    }

    public ChannelConnectorKind Kind => ChannelConnectorKind.Telegram;

    public async Task StartAsync(
        ChannelAccount account,
        Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(onEvent);
        cancellationToken.ThrowIfCancellationRequested();

        if (account.ConnectorKind != ChannelConnectorKind.Telegram)
        {
            throw new ArgumentException(
                $"Telegram connector cannot start account with connector kind '{account.ConnectorKind}'.",
                nameof(account));
        }

        var accountId = ValidateAndNormalizeAccountId(account.Id);
        var configuration = await TelegramConnectorConfiguration
            .FromAccountAsync(account, _secretResolver, cancellationToken)
            .ConfigureAwait(false);
        await _apiClient.GetMeAsync(configuration.BotToken, cancellationToken).ConfigureAwait(false);

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var startedAccount = new StartedAccount(
            account: account with { Id = accountId },
            configuration: configuration,
            onEvent: onEvent,
            cancellationTokenSource: linkedCts);
        if (!_startedAccounts.TryAdd(accountId, startedAccount))
        {
            linkedCts.Dispose();
            throw new InvalidOperationException($"Telegram account '{accountId}' is already started.");
        }

        startedAccount.PollingTask = Task.Run(
            () => RunPollingLoopAsync(startedAccount),
            CancellationToken.None);

        RecordDiagnosticEvent("telegram.account_started", "info", $"Telegram account started: accountId={accountId}");
    }

    public async Task StopAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = ValidateAndNormalizeAccountId(accountId);

        if (!_startedAccounts.TryRemove(normalizedAccountId, out var startedAccount))
        {
            return;
        }

        startedAccount.CancellationTokenSource.Cancel();
        try
        {
            await startedAccount.PollingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (startedAccount.CancellationTokenSource.IsCancellationRequested)
        {
        }
        finally
        {
            startedAccount.CancellationTokenSource.Dispose();
        }

        RecordDiagnosticEvent("telegram.account_stopped", "info", $"Telegram account stopped: accountId={accountId}");
    }

    public async Task EnsureStartedAndSendAsync(
        ChannelAccount account,
        ChannelOutboundDraft draft,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await StartAsync(account, static (_, _) => Task.CompletedTask, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Already started by inbound path
        }
        await SendAsync(draft, cancellationToken);
    }

    public async Task SendAsync(ChannelOutboundDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.ConnectorKind != ChannelConnectorKind.Telegram)
        {
            throw new ArgumentException(
                $"Telegram connector cannot send draft with connector kind '{draft.ConnectorKind}'.",
                nameof(draft));
        }

        var accountId = ValidateAndNormalizeAccountId(draft.AccountId);
        if (!_startedAccounts.TryGetValue(accountId, out var startedAccount))
        {
            throw new InvalidOperationException(
                $"Telegram account '{accountId}' must be started before outbound delivery.");
        }

        if (!long.TryParse(
                draft.ExternalThreadId,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var chatId))
        {
            throw new ArgumentException(
                "Telegram outbound draft must provide a numeric external thread id.",
                nameof(draft));
        }

        if (string.IsNullOrWhiteSpace(draft.MessageText))
        {
            throw new ArgumentException("Telegram outbound draft message text is required.", nameof(draft));
        }

        var imageAttachment = draft.MediaAttachments?.FirstOrDefault(
            static a => a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

        if (imageAttachment is not null && _mediaStore is not null)
        {
            var stream = await _mediaStore.OpenReadAsync(imageAttachment.MediaId, cancellationToken)
                .ConfigureAwait(false);
            if (stream is not null)
            {
                await using (stream.ConfigureAwait(false))
                {
                    await _apiClient.SendPhotoAsync(
                        startedAccount.Configuration.BotToken,
                        chatId,
                        stream,
                        imageAttachment.ContentType,
                        caption: draft.MessageText,
                        cancellationToken).ConfigureAwait(false);
                }

                return;
            }
        }

        var audioAttachment = draft.MediaAttachments?.FirstOrDefault(
            static a => a.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

        if (audioAttachment is not null && _mediaStore is not null)
        {
            var stream = await _mediaStore.OpenReadAsync(audioAttachment.MediaId, cancellationToken)
                .ConfigureAwait(false);
            if (stream is not null)
            {
                await using (stream.ConfigureAwait(false))
                {
                    await _apiClient.SendAudioAsync(
                        startedAccount.Configuration.BotToken,
                        chatId,
                        stream,
                        audioAttachment.ContentType,
                        caption: draft.MessageText,
                        cancellationToken).ConfigureAwait(false);
                }

                return;
            }
        }

        var parseMode = draft.Format == OutboundMessageFormat.Markdown ? "Markdown" : null;
        await _apiClient.SendMessageAsync(
            startedAccount.Configuration.BotToken,
            chatId,
            draft.MessageText,
            parseMode,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunPollingLoopAsync(StartedAccount startedAccount)
    {
        long? nextOffset = null;
        var token = startedAccount.CancellationTokenSource.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var updates = await _apiClient.GetUpdatesAsync(
                    startedAccount.Configuration.BotToken,
                    nextOffset,
                    startedAccount.Configuration.LongPollingTimeoutSeconds,
                    token).ConfigureAwait(false);

                if (updates.Count == 0)
                {
                    await Task.Delay(_options.IdleDelay, token).ConfigureAwait(false);
                    continue;
                }

                foreach (var update in updates.OrderBy(static item => item.UpdateId))
                {
                    var candidateOffset = update.UpdateId + 1;
                    nextOffset = nextOffset.HasValue
                        ? Math.Max(nextOffset.Value, candidateOffset)
                        : candidateOffset;

                    var envelope = TryMapToEnvelope(startedAccount.Account, startedAccount.Configuration, update);
                    if (envelope is null)
                    {
                        continue;
                    }

                    await startedAccount.OnEvent(envelope, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent(
                    "telegram.polling_error",
                    "warning",
                    $"Telegram polling error (retrying): accountId={startedAccount.Account.Id} error={ex.GetBaseException().Message}");
                try
                {
                    await Task.Delay(_options.ErrorRetryDelay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private ChannelEventEnvelope? TryMapToEnvelope(ChannelAccount account, TelegramConnectorConfiguration configuration, TelegramUpdate update)
    {
        if (update is null)
        {
            return null;
        }

        var eventType = default(ChannelEventType?);
        TelegramMessage? message = null;
        if (update.Message is not null)
        {
            eventType = ChannelEventType.MessageReceived;
            message = update.Message;
        }
        else if (update.EditedMessage is not null)
        {
            eventType = ChannelEventType.MessageEdited;
            message = update.EditedMessage;
        }

        if (!eventType.HasValue || message?.Chat is null)
        {
            return null;
        }

        var chat = message.Chat;
        var text = message.GetText();
        IReadOnlyList<MediaReference>? mediaAttachments = null;
        var externalMessageId = message.MessageId.ToString(CultureInfo.InvariantCulture);

        // 媒体消息处理：生成占位文本 + MediaReference（不实际下载文件）
        if (string.IsNullOrWhiteSpace(text))
        {
            if (message.Photo is { Count: > 0 })
            {
                // 取最大尺寸的 photo（通常是最后一个）
                var photo = message.Photo[message.Photo.Count - 1];
                text = "[图片]";
                mediaAttachments =
                [
                    new MediaReference(
                        MediaId: photo.FileId,
                        ContentType: "image/jpeg",
                        FileName: null,
                        SizeBytes: photo.FileSize)
                ];
            }
            else if (message.Document is { } doc)
            {
                var mimeType = doc.MimeType ?? "application/octet-stream";
                if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                {
                    text = "[语音]";
                    mediaAttachments =
                    [
                        new MediaReference(
                            MediaId: doc.FileId,
                            ContentType: mimeType,
                            FileName: doc.FileName,
                            SizeBytes: doc.FileSize)
                    ];
                }
                else if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                {
                    text = "[视频]";
                    mediaAttachments =
                    [
                        new MediaReference(
                            MediaId: doc.FileId,
                            ContentType: mimeType,
                            FileName: doc.FileName,
                            SizeBytes: doc.FileSize)
                    ];
                }
                else
                {
                    text = $"[文件: {doc.FileName ?? doc.FileId}]";
                    mediaAttachments =
                    [
                        new MediaReference(
                            MediaId: doc.FileId,
                            ContentType: mimeType,
                            FileName: doc.FileName,
                            SizeBytes: doc.FileSize)
                    ];
                }
            }
        }

        // IgnoreNonTextMessages 仍然适用于既无文本也无可识别媒体的消息
        if (_options.IgnoreNonTextMessages && string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var threadType = ResolveThreadType(chat.Type);
        var occurredAt = message.DateUnixSeconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(message.DateUnixSeconds)
            : DateTimeOffset.UtcNow;
        var externalThreadId = chat.Id.ToString(CultureInfo.InvariantCulture);

        return new ChannelEventEnvelope(
            EventId: $"telegram-{update.UpdateId.ToString(CultureInfo.InvariantCulture)}",
            EventType: eventType.Value,
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: account.Id,
            ExternalThreadId: externalThreadId,
            ThreadType: threadType,
            OccurredAt: occurredAt,
            Sender: MapSender(message.From),
            Recipient: MapRecipient(chat),
            ExternalMessageId: externalMessageId,
            Text: text,
            DefaultDeliveryMode: configuration.DefaultDeliveryMode,
            MediaAttachments: mediaAttachments);
    }

    private static ChannelThreadType ResolveThreadType(string? chatType)
    {
        return string.Equals(chatType, "private", StringComparison.OrdinalIgnoreCase)
            ? ChannelThreadType.DirectMessage
            : ChannelThreadType.Group;
    }

    private static ChannelIdentity? MapSender(TelegramUser? sender)
    {
        if (sender is null)
        {
            return null;
        }

        var displayName = BuildDisplayName(sender.FirstName, sender.LastName);
        return new ChannelIdentity(
            Id: sender.Id.ToString(CultureInfo.InvariantCulture),
            Username: sender.Username,
            DisplayName: displayName,
            IsBot: sender.IsBot);
    }

    private static ChannelIdentity MapRecipient(TelegramChat chat)
    {
        var displayName = chat.Title ?? chat.Username;
        return new ChannelIdentity(
            Id: chat.Id.ToString(CultureInfo.InvariantCulture),
            Username: chat.Username,
            DisplayName: displayName);
    }

    private static string? BuildDisplayName(string? firstName, string? lastName)
    {
        var normalizedFirst = string.IsNullOrWhiteSpace(firstName) ? null : firstName.Trim();
        var normalizedLast = string.IsNullOrWhiteSpace(lastName) ? null : lastName.Trim();

        if (normalizedFirst is null && normalizedLast is null)
        {
            return null;
        }

        if (normalizedFirst is null)
        {
            return normalizedLast;
        }

        if (normalizedLast is null)
        {
            return normalizedFirst;
        }

        return $"{normalizedFirst} {normalizedLast}";
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

    private sealed class StartedAccount
    {
        public StartedAccount(
            ChannelAccount account,
            TelegramConnectorConfiguration configuration,
            Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
            CancellationTokenSource cancellationTokenSource)
        {
            Account = account;
            Configuration = configuration;
            OnEvent = onEvent;
            CancellationTokenSource = cancellationTokenSource;
        }

        public ChannelAccount Account { get; }

        public TelegramConnectorConfiguration Configuration { get; }

        public Func<ChannelEventEnvelope, CancellationToken, Task> OnEvent { get; }

        public CancellationTokenSource CancellationTokenSource { get; }

        public Task PollingTask { get; set; } = Task.CompletedTask;
    }
}
