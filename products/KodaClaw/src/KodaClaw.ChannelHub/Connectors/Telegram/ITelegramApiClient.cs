namespace KodaClaw.ChannelHub.Connectors.Telegram;

public interface ITelegramApiClient
{
    Task<TelegramUser> GetMeAsync(
        string botToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        string botToken,
        long? offset,
        int timeoutSeconds,
        CancellationToken cancellationToken = default);

    Task<TelegramSendMessageResult> SendMessageAsync(
        string botToken,
        long chatId,
        string text,
        string? parseMode = null,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default);

    Task<TelegramSendMessageResult> SendPhotoAsync(
        string botToken,
        long chatId,
        Stream photo,
        string contentType,
        string? caption,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default);

    Task<TelegramSendMessageResult> SendAudioAsync(
        string botToken,
        long chatId,
        Stream audio,
        string contentType,
        string? caption,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送视频消息（multipart 上传）。
    /// POST https://api.telegram.org/bot{token}/sendVideo
    /// 官方文档：https://core.telegram.org/bots/api#sendvideo
    /// </summary>
    Task<TelegramSendMessageResult> SendVideoAsync(
        string botToken,
        long chatId,
        Stream video,
        string contentType,
        string? caption,
        int? durationSeconds = null,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default);

    Task<TelegramSendMessageResult> EditMessageTextAsync(
        string botToken,
        long chatId,
        long messageId,
        string text,
        string? parseMode = null,
        CancellationToken cancellationToken = default);
}
