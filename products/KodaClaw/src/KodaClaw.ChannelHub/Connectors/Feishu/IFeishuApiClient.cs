using KodaClaw.ChannelHub.Connectors.Feishu.Models;

namespace KodaClaw.ChannelHub.Connectors.Feishu;

public interface IFeishuApiClient
{
    /// <summary>获取 tenant_access_token（用于发消息）</summary>
    Task<string> GetTenantAccessTokenAsync(
        string appId,
        string appSecret,
        CancellationToken cancellationToken = default);

    /// <summary>获取 app_access_token（用于 WS 长连接认证）</summary>
    Task<string> GetAppAccessTokenAsync(
        string appId,
        string appSecret,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取 WebSocket 长连接 endpoint URL 及客户端配置。
    /// POST /callback/ws/endpoint with {AppID, AppSecret}。
    /// </summary>
    Task<FeishuWsEndpoint> GetWsEndpointAsync(
        string appId,
        string appSecret,
        CancellationToken cancellationToken = default);

    /// <summary>发送文本消息</summary>
    Task<string> SendTextMessageAsync(
        string accessToken,
        string receiveId,
        string receiveIdType,
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>上传图片，返回 image_key</summary>
    Task<string> UploadImageAsync(
        string accessToken,
        Stream image,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>发送图片消息（使用已上传的 image_key）</summary>
    Task<string> SendImageMessageAsync(
        string accessToken,
        string receiveId,
        string receiveIdType,
        string imageKey,
        string? caption,
        CancellationToken cancellationToken = default);

    /// <summary>上传音频文件，返回 file_key</summary>
    Task<string> UploadAudioFileAsync(
        string accessToken,
        Stream audio,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>发送音频消息（使用已上传的 file_key）</summary>
    Task<string> SendAudioMessageAsync(
        string accessToken,
        string receiveId,
        string receiveIdType,
        string fileKey,
        string? caption,
        CancellationToken cancellationToken = default);

    /// <summary>上传视频文件，返回 file_key</summary>
    Task<string> UploadVideoFileAsync(
        string accessToken,
        Stream video,
        string contentType,
        int? durationMs = null,
        CancellationToken cancellationToken = default);

    /// <summary>发送视频消息（使用已上传的 file_key）</summary>
    Task<string> SendVideoMessageAsync(
        string accessToken,
        string receiveId,
        string receiveIdType,
        string fileKey,
        string? caption,
        CancellationToken cancellationToken = default);

    /// <summary>发送 Post 富文本消息</summary>
    Task<string> SendPostMessageAsync(
        string tenantAccessToken,
        string receiveId,
        string receiveIdType,
        string title,
        List<FeishuPostContent> content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 编辑已发送的文本消息（用于 progress indicator edit-in-place）。
    /// PUT /open-apis/im/v1/messages/{message_id} with {"msg_type":"text","content":"{\"text\":...}"}
    /// 飞书官方要求 PUT（不是 PATCH）：https://open.feishu.cn/document/server-docs/im-v1/message/update
    /// </summary>
    Task PatchTextMessageAsync(
        string tenantAccessToken,
        string messageId,
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Download a media resource (image/file) from a Feishu message.
    /// GET /open-apis/im/v1/messages/{messageId}/resources/{fileKey}?type={type}
    /// Returns a stream that the caller must dispose.
    /// </summary>
    Task<Stream> DownloadResourceAsync(
        string accessToken,
        string messageId,
        string fileKey,
        string type,
        CancellationToken cancellationToken = default);
}