using System.Net.Http.Json;
using System.Text.Json;

namespace KodaClaw.ChannelHub.Connectors.Telegram;

public sealed class HttpTelegramApiClient : ITelegramApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    public HttpTelegramApiClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<TelegramUser> GetMeAsync(
        string botToken,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<TelegramUser>(
            botToken,
            method: "getMe",
            payload: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        string botToken,
        long? offset,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<List<TelegramUpdate>>(
            botToken,
            method: "getUpdates",
            payload: new
            {
                offset,
                timeout = timeoutSeconds,
                allowed_updates = new[] { "message", "edited_message" },
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TelegramSendMessageResult> SendMessageAsync(
        string botToken,
        long chatId,
        string text,
        string? parseMode = null,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A non-empty telegram message text is required.", nameof(text));
        }

        object payload = BuildTextPayload(chatId, text, parseMode, replyToMessageId);

        return await SendAsync<TelegramSendMessageResult>(
            botToken,
            method: "sendMessage",
            payload: payload,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TelegramSendMessageResult> SendPhotoAsync(
        string botToken,
        long chatId,
        Stream photo,
        string contentType,
        string? caption,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new ArgumentException("A non-empty telegram bot token is required.", nameof(botToken));
        }

        ArgumentNullException.ThrowIfNull(photo);

        var ext = contentType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");
        var photoContent = new StreamContent(photo);
        photoContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(photoContent, "photo", $"photo.{ext}");
        if (!string.IsNullOrWhiteSpace(caption))
        {
            form.Add(new StringContent(caption), "caption");
        }
        AddReplyParameters(form, replyToMessageId);

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(botToken, "sendPhoto"))
        {
            Content = form,
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content
            .ReadFromJsonAsync<TelegramApiResponse<TelegramSendMessageResult>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (envelope is null || !envelope.Ok || envelope.Result is null)
        {
            throw new InvalidOperationException(
                $"Telegram API 'sendPhoto' failed: {envelope?.Description ?? "unknown error"}");
        }

        return envelope.Result;
    }

    public async Task<TelegramSendMessageResult> SendAudioAsync(
        string botToken,
        long chatId,
        Stream audio,
        string contentType,
        string? caption,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new ArgumentException("A non-empty telegram bot token is required.", nameof(botToken));
        }

        ArgumentNullException.ThrowIfNull(audio);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");
        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(audioContent, "audio", "speech.mp3");
        if (!string.IsNullOrWhiteSpace(caption))
        {
            form.Add(new StringContent(caption), "caption");
        }
        AddReplyParameters(form, replyToMessageId);

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(botToken, "sendAudio"))
        {
            Content = form,
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content
            .ReadFromJsonAsync<TelegramApiResponse<TelegramSendMessageResult>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (envelope is null || !envelope.Ok || envelope.Result is null)
        {
            throw new InvalidOperationException(
                $"Telegram API 'sendAudio' failed: {envelope?.Description ?? "unknown error"}");
        }

        return envelope.Result;
    }

    public async Task<TelegramSendMessageResult> SendVideoAsync(
        string botToken,
        long chatId,
        Stream video,
        string contentType,
        string? caption,
        int? durationSeconds = null,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new ArgumentException("A non-empty telegram bot token is required.", nameof(botToken));
        }

        ArgumentNullException.ThrowIfNull(video);

        var ext = contentType.Contains("mp4", StringComparison.OrdinalIgnoreCase) ? "mp4"
            : contentType.Contains("quicktime", StringComparison.OrdinalIgnoreCase) ? "mov"
            : contentType.Contains("webm", StringComparison.OrdinalIgnoreCase) ? "webm"
            : "mp4";

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");
        var videoContent = new StreamContent(video);
        videoContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(videoContent, "video", $"video.{ext}");
        if (!string.IsNullOrWhiteSpace(caption))
        {
            form.Add(new StringContent(caption), "caption");
        }
        if (durationSeconds is > 0)
        {
            form.Add(
                new StringContent(durationSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "duration");
        }
        AddReplyParameters(form, replyToMessageId);
        // supports_streaming=true 让 Telegram 客户端对 MP4 采取流式播放（未知容器会被忽略）。
        form.Add(new StringContent("true"), "supports_streaming");

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(botToken, "sendVideo"))
        {
            Content = form,
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content
            .ReadFromJsonAsync<TelegramApiResponse<TelegramSendMessageResult>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (envelope is null || !envelope.Ok || envelope.Result is null)
        {
            throw new InvalidOperationException(
                $"Telegram API 'sendVideo' failed: {envelope?.Description ?? "unknown error"}");
        }

        return envelope.Result;
    }

    public async Task<TelegramSendMessageResult> EditMessageTextAsync(
        string botToken,
        long chatId,
        long messageId,
        string text,
        string? parseMode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A non-empty telegram message text is required.", nameof(text));
        }

        object payload = string.IsNullOrEmpty(parseMode)
            ? new { chat_id = chatId, message_id = messageId, text }
            : new { chat_id = chatId, message_id = messageId, text, parse_mode = parseMode };

        return await SendAsync<TelegramSendMessageResult>(
            botToken,
            method: "editMessageText",
            payload: payload,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(
        string botToken,
        string method,
        object? payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new ArgumentException("A non-empty telegram bot token is required.", nameof(botToken));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildEndpoint(botToken, method));
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: JsonOptions);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content
            .ReadFromJsonAsync<TelegramApiResponse<T>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (envelope is null)
        {
            throw new InvalidOperationException($"Telegram API '{method}' returned an empty response payload.");
        }

        if (!envelope.Ok || envelope.Result is null)
        {
            throw new InvalidOperationException(
                $"Telegram API '{method}' failed: {envelope.Description ?? "unknown error"}");
        }

        return envelope.Result;
    }

    private static string BuildEndpoint(string botToken, string method)
    {
        return $"https://api.telegram.org/bot{botToken.Trim()}/{method}";
    }

    private static object BuildTextPayload(
        long chatId,
        string text,
        string? parseMode,
        long? replyToMessageId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = text,
        };
        if (!string.IsNullOrEmpty(parseMode))
        {
            payload["parse_mode"] = parseMode;
        }
        if (replyToMessageId.HasValue)
        {
            payload["reply_parameters"] = new { message_id = replyToMessageId.Value };
        }

        return payload;
    }

    private static void AddReplyParameters(
        MultipartFormDataContent form,
        long? replyToMessageId)
    {
        if (!replyToMessageId.HasValue)
        {
            return;
        }

        var value = JsonSerializer.Serialize(new { message_id = replyToMessageId.Value }, JsonOptions);
        form.Add(new StringContent(value), "reply_parameters");
    }
}
