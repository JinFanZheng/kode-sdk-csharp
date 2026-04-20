using System.Text.Json.Serialization;

namespace KodaClaw.ChannelHub.Connectors.WeChat;

// ── 接收消息 ──────────────────────────────────────────────

public sealed class ILinkGetUpdatesResponse
{
    [JsonPropertyName("ret")]
    public int Ret { get; init; }

    [JsonPropertyName("msgs")]
    public IReadOnlyList<ILinkMessage> Msgs { get; init; } = [];

    [JsonPropertyName("get_updates_buf")]
    public string GetUpdatesBuf { get; init; } = string.Empty;

    [JsonPropertyName("longpolling_timeout_ms")]
    public int LongPollingTimeoutMs { get; init; }
}

public sealed class ILinkMessage
{
    [JsonPropertyName("message_id")]
    public long MessageId { get; init; }

    [JsonPropertyName("from_user_id")]
    public string FromUserId { get; init; } = string.Empty;

    [JsonPropertyName("context_token")]
    public string ContextToken { get; init; } = string.Empty;

    [JsonPropertyName("item_list")]
    public IReadOnlyList<ILinkMessageItem> ItemList { get; init; } = [];
}

public sealed class ILinkMessageItem
{
    [JsonPropertyName("type")]
    public int Type { get; init; }

    [JsonPropertyName("text_item")]
    public ILinkTextItem? TextItem { get; init; }

    [JsonPropertyName("image_item")]
    public ILinkImageItem? ImageItem { get; init; }

    [JsonPropertyName("file_item")]
    public ILinkFileItem? FileItem { get; init; }

    [JsonPropertyName("video_item")]
    public ILinkVideoItem? VideoItem { get; init; }
}

public sealed class ILinkTextItem
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}

// ── 媒体通用结构 ──────────────────────────────────────────────

public sealed class ILinkMedia
{
    [JsonPropertyName("encrypt_query_param")]
    public string EncryptQueryParam { get; init; } = string.Empty;

    [JsonPropertyName("aes_key")]
    public string AesKey { get; init; } = string.Empty;

    [JsonPropertyName("encrypt_type")]
    public int EncryptType { get; init; } = 1;
}

public sealed class ILinkImageItem
{
    [JsonPropertyName("media")]
    public ILinkMedia? Media { get; init; }
}

public sealed class ILinkFileItem
{
    [JsonPropertyName("media")]
    public ILinkMedia? Media { get; init; }

    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("len")]
    public string Len { get; init; } = "0";
}

public sealed class ILinkVideoItem
{
    [JsonPropertyName("media")]
    public ILinkMedia? Media { get; init; }

    [JsonPropertyName("video_size")]
    public long VideoSize { get; init; }
}

// ── 上传媒体 ──────────────────────────────────────────────────

public sealed class ILinkGetUploadUrlRequest
{
    [JsonPropertyName("filekey")]
    public required string FileKey { get; init; }

    [JsonPropertyName("media_type")]
    public int MediaType { get; init; } // 1=图片 2=视频 3=文件 4=语音

    [JsonPropertyName("to_user_id")]
    public required string ToUserId { get; init; }

    [JsonPropertyName("rawsize")]
    public long RawSize { get; init; }

    [JsonPropertyName("rawfilemd5")]
    public required string RawFileMd5 { get; init; }

    [JsonPropertyName("filesize")]
    public long FileSize { get; init; }

    [JsonPropertyName("aeskey")]
    public required string AesKey { get; init; }
}

public sealed class ILinkGetUploadUrlResponse
{
    [JsonPropertyName("upload_param")]
    public ILinkUploadParam? UploadParam { get; init; }
}

public sealed class ILinkUploadParam
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }
}

// ── 发送消息 ──────────────────────────────────────────────

public sealed class ILinkSendMessageRequest
{
    [JsonPropertyName("msg")]
    public required ILinkSendMessageBody Msg { get; init; }

    [JsonPropertyName("base_info")]
    public ILinkBaseInfo BaseInfo { get; init; } = new();
}

public sealed class ILinkSendMessageBody
{
    [JsonPropertyName("from_user_id")]
    public string FromUserId { get; init; } = string.Empty;

    [JsonPropertyName("to_user_id")]
    public required string ToUserId { get; init; }

    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("context_token")]
    public required string ContextToken { get; init; }

    [JsonPropertyName("message_type")]
    public int MessageType { get; init; } = 2; // 2 = Bot 消息

    [JsonPropertyName("message_state")]
    public int MessageState { get; init; } = 2; // 2 = FINISH

    [JsonPropertyName("item_list")]
    public required IReadOnlyList<ILinkMessageItem> ItemList { get; init; }
}

public sealed class ILinkBaseInfo
{
    [JsonPropertyName("channel_version")]
    public string ChannelVersion { get; init; } = "kodaclaw";
}

// ── 登录 ──────────────────────────────────────────────────

public sealed class ILinkQrCodeResponse
{
    [JsonPropertyName("qrcode")]
    public string Qrcode { get; init; } = string.Empty;

    [JsonPropertyName("qrcode_img_content")]
    public string QrcodeImgContent { get; init; } = string.Empty;
}

public sealed class ILinkQrCodeStatusResponse
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("bot_token")]
    public string? BotToken { get; init; }

    [JsonPropertyName("ilink_bot_id")]
    public string? ILinkBotId { get; init; }

    [JsonPropertyName("ilink_user_id")]
    public string? ILinkUserId { get; init; }
}

public sealed class ILinkLoginStatusResponse
{
    [JsonPropertyName("ret")]
    public int Ret { get; init; }
}

// ── 正在输入 ──────────────────────────────────────────────

public sealed class ILinkGetConfigResponse
{
    [JsonPropertyName("ret")]
    public int Ret { get; init; }

    [JsonPropertyName("typing_ticket")]
    public string TypingTicket { get; init; } = string.Empty;
}
