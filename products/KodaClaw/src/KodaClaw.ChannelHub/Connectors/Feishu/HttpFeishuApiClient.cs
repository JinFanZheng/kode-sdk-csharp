using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KodaClaw.ChannelHub.Common;
using KodaClaw.ChannelHub.Connectors.Feishu.Models;

namespace KodaClaw.ChannelHub.Connectors.Feishu;

public sealed class HttpFeishuApiClient : IFeishuApiClient
{
    private const string BaseUrl = "https://open.feishu.cn";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly TokenCache _tokenCache = new();

    public HttpFeishuApiClient()
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(BaseUrl) };
    }

    public async Task<string> GetTenantAccessTokenAsync(
        string appId,
        string appSecret,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"tenant:{appId}";
        return await _tokenCache.GetOrRefreshAsync(cacheKey, async ct =>
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/open-apis/auth/v3/tenant_access_token/internal");
            request.Content = JsonContent.Create(
                new { app_id = appId, app_secret = appSecret },
                options: JsonOptions);

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var result = await response.Content
                .ReadFromJsonAsync<FeishuTenantAccessTokenResponse>(JsonOptions, ct)
                .ConfigureAwait(false);

            if (result is null || result.Code != 0 || string.IsNullOrWhiteSpace(result.TenantAccessToken))
            {
                throw new InvalidOperationException(
                    $"Failed to get Feishu tenant_access_token: code={result?.Code}, msg={result?.Msg}");
            }

            return (result.TenantAccessToken, result.ExpireSeconds);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetAppAccessTokenAsync(
        string appId,
        string appSecret,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"app:{appId}";
        return await _tokenCache.GetOrRefreshAsync(cacheKey, async ct =>
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/open-apis/auth/v3/app_access_token/internal");
            request.Content = JsonContent.Create(
                new { app_id = appId, app_secret = appSecret },
                options: JsonOptions);

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var result = await response.Content
                .ReadFromJsonAsync<FeishuAppAccessTokenResponse>(JsonOptions, ct)
                .ConfigureAwait(false);

            if (result is null || result.Code != 0 || string.IsNullOrWhiteSpace(result.AppAccessToken))
            {
                throw new InvalidOperationException(
                    $"Failed to get Feishu app_access_token: code={result?.Code}, msg={result?.Msg}");
            }

            return (result.AppAccessToken, result.ExpireSeconds);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> SendTextMessageAsync(
        string accessToken,
        string receiveId,
        string receiveIdType,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text is required.", nameof(text));
        }

        var content = JsonSerializer.Serialize(new { text }, JsonOptions);
        using var request = BuildSendMessageRequest(accessToken, receiveId, receiveIdType, "text", content);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await ParseSendMessageResponse(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReplyTextMessageAsync(
        string accessToken,
        string messageId,
        string text,
        bool replyInThread = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text is required.", nameof(text));
        }
        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new ArgumentException("Message id is required.", nameof(messageId));
        }

        var content = JsonSerializer.Serialize(new { text }, JsonOptions);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/open-apis/im/v1/messages/{Uri.EscapeDataString(messageId)}/reply");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(
            new
            {
                msg_type = "text",
                content,
                reply_in_thread = replyInThread,
            },
            options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await ParseSendMessageResponse(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> UploadImageAsync(
        string accessToken,
        Stream image,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("message"), "image_type");
        var imageContent = new StreamContent(image);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(imageContent, "image", "image");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/open-apis/im/v1/images");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content
            .ReadFromJsonAsync<FeishuUploadImageResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (result is null || result.Code != 0 || string.IsNullOrWhiteSpace(result.Data?.ImageKey))
        {
            throw new InvalidOperationException(
                $"Feishu upload image failed: code={result?.Code}, msg={result?.Msg}");
        }

        return result.Data.ImageKey;
    }

    public async Task<string> SendImageMessageAsync(
        string accessToken,
        string receiveId,
        string receiveIdType,
        string imageKey,
        string? caption,
        CancellationToken cancellationToken = default)
    {
        // 飞书图片消息不支持 caption；若有 caption 先发图再发文字
        var content = JsonSerializer.Serialize(new { image_key = imageKey }, JsonOptions);
        using var request = BuildSendMessageRequest(accessToken, receiveId, receiveIdType, "image", content);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var responseText = System.Text.Encoding.UTF8.GetString(responseBytes);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Feishu send image message failed: status={(int)response.StatusCode}, body={responseText}");
        }

        var messageId = ParseSendMessageResponseFromBytes(responseBytes, cancellationToken);

        if (!string.IsNullOrWhiteSpace(caption))
        {
            await SendTextMessageAsync(accessToken, receiveId, receiveIdType, caption, cancellationToken)
                .ConfigureAwait(false);
        }

        return messageId;
    }

    public async Task<string> UploadAudioFileAsync(
        string accessToken,
        Stream audio,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("opus"), "file_type");
        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(audioContent, "file", "speech.mp3");
        form.Add(new StringContent("speech.mp3"), "file_name");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/open-apis/im/v1/files");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content
            .ReadFromJsonAsync<FeishuUploadFileResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (result is null || result.Code != 0 || string.IsNullOrWhiteSpace(result.Data?.FileKey))
        {
            throw new InvalidOperationException(
                $"Feishu upload audio failed: code={result?.Code}, msg={result?.Msg}");
        }

        return result.Data.FileKey;
    }

    public async Task<string> SendAudioMessageAsync(
        string accessToken,
        string receiveId,
        string receiveIdType,
        string fileKey,
        string? caption,
        CancellationToken cancellationToken = default)
    {
        var content = JsonSerializer.Serialize(new { file_key = fileKey }, JsonOptions);
        using var request = BuildSendMessageRequest(accessToken, receiveId, receiveIdType, "audio", content);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var responseText = System.Text.Encoding.UTF8.GetString(responseBytes);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Feishu send audio message failed: status={(int)response.StatusCode}, body={responseText}");
        }

        var messageId = ParseSendMessageResponseFromBytes(responseBytes, cancellationToken);

        if (!string.IsNullOrWhiteSpace(caption))
        {
            await SendTextMessageAsync(accessToken, receiveId, receiveIdType, caption, cancellationToken)
                .ConfigureAwait(false);
        }

        return messageId;
    }

    public async Task<string> UploadVideoFileAsync(
        string accessToken,
        Stream video,
        string contentType,
        int? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("mp4"), "file_type");
        var videoContent = new StreamContent(video);
        videoContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(videoContent, "file", "video.mp4");
        form.Add(new StringContent("video.mp4"), "file_name");
        if (durationMs.HasValue)
        {
            form.Add(new StringContent(durationMs.Value.ToString()), "duration");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/open-apis/im/v1/files");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", accessToken);
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var responseText = System.Text.Encoding.UTF8.GetString(responseBytes);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Feishu upload video failed: status={(int)response.StatusCode}, body={responseText}");
        }

        var result = JsonSerializer.Deserialize<FeishuUploadFileResponse>(responseBytes, JsonOptions);

        if (result is null || result.Code != 0 || string.IsNullOrWhiteSpace(result.Data?.FileKey))
        {
            throw new InvalidOperationException(
                $"Feishu upload video failed: code={result?.Code}, msg={result?.Msg}, body={responseText}");
        }
        return result.Data.FileKey;
    }

    public async Task<string> SendVideoMessageAsync(
        string accessToken,
        string receiveId,
        string receiveIdType,
        string fileKey,
        string? caption,
        CancellationToken cancellationToken = default)
    {
        var content = JsonSerializer.Serialize(new { file_key = fileKey }, JsonOptions);
        using var request = BuildSendMessageRequest(accessToken, receiveId, receiveIdType, "media", content);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var responseText = System.Text.Encoding.UTF8.GetString(responseBytes);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Feishu send video message failed: status={(int)response.StatusCode}, body={responseText}");
        }

        var messageId = ParseSendMessageResponseFromBytes(responseBytes, cancellationToken);

        if (!string.IsNullOrWhiteSpace(caption))
        {
            await SendTextMessageAsync(accessToken, receiveId, receiveIdType, caption, cancellationToken)
                .ConfigureAwait(false);
        }

        return messageId;
    }

    public async Task<string> SendPostMessageAsync(
        string tenantAccessToken,
        string receiveId,
        string receiveIdType,
        string title,
        List<FeishuPostContent> content,
        CancellationToken cancellationToken = default)
    {
        // 将 content 列表序列化为飞书 Post 期望的格式：
        // content 字段是 JSON 字符串，结构为 {"zh_cn":{"title":"...","content":[[elem],[elem],...]}
        // 每个元素单独作为一行（一个 row = 一个单元素列表）
        var rows = content.Select(elem =>
        {
            var row = new Dictionary<string, object?> { ["tag"] = elem.Tag };
            if (elem.Text is not null) row["text"] = elem.Text;
            if (elem.Href is not null) row["href"] = elem.Href;
            if (elem.Language is not null) row["language"] = elem.Language;
            return new List<Dictionary<string, object?>> { row };
        }).ToList();

        var postBody = new
        {
            zh_cn = new
            {
                title,
                content = rows,
            }
        };

        var contentJson = JsonSerializer.Serialize(postBody, JsonOptions);
        using var request = BuildSendMessageRequest(tenantAccessToken, receiveId, receiveIdType, "post", contentJson);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await ParseSendMessageResponse(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task PatchTextMessageAsync(
        string tenantAccessToken,
        string messageId,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantAccessToken))
        {
            throw new ArgumentException("A non-empty tenant access token is required.", nameof(tenantAccessToken));
        }
        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new ArgumentException("A non-empty message id is required.", nameof(messageId));
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A non-empty text is required.", nameof(text));
        }

        // 飞书消息编辑 API 要求 PUT（不是 PATCH）。
        // https://open.feishu.cn/document/server-docs/im-v1/message/update
        var content = JsonSerializer.Serialize(new { text }, JsonOptions);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/open-apis/im/v1/messages/{Uri.EscapeDataString(messageId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenantAccessToken);
        request.Content = JsonContent.Create(
            new
            {
                msg_type = "text",
                content,
            },
            options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = System.Text.Encoding.UTF8.GetString(responseBytes);
            throw new HttpRequestException(
                $"Feishu edit message failed: status={(int)response.StatusCode}, body={body}");
        }

        var result = JsonSerializer.Deserialize<FeishuSendMessageResponse>(responseBytes, JsonOptions);
        if (result is null || result.Code != 0)
        {
            var body = System.Text.Encoding.UTF8.GetString(responseBytes);
            throw new InvalidOperationException(
                $"Feishu edit message failed: code={result?.Code}, msg={result?.Msg}, body={body}");
        }
    }

    public async Task<FeishuWsEndpoint> GetWsEndpointAsync(
        string appId,
        string appSecret,
        CancellationToken cancellationToken = default)
    {
        // 注意：此 API 路径是 /callback/ws/endpoint，不在 /open-apis/ 下。
        // 请求体字段名是 AppID / AppSecret（Pascal case）。
        using var request = new HttpRequestMessage(HttpMethod.Post, "/callback/ws/endpoint");
        // 飞书要求 Pascal case 字段名（AppID / AppSecret），
        // 直接用 StringContent 避免 JsonContent 附加 charset 或命名策略干扰。
        var bodyJson = System.Text.Json.JsonSerializer.Serialize(new { AppID = appId, AppSecret = appSecret });
        request.Content = new StringContent(bodyJson);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("locale", "zh");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"GET /callback/ws/endpoint failed: {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
        }

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = doc.RootElement;

        if (!root.TryGetProperty("code", out var codeProp) || codeProp.GetInt32() != 0)
        {
            var msg = root.TryGetProperty("msg", out var msgProp) ? msgProp.GetString() : "unknown";
            throw new InvalidOperationException($"Failed to get Feishu WS endpoint: {msg}");
        }

        var data = root.GetProperty("data");
        var url = data.GetProperty("URL").GetString()
            ?? throw new InvalidOperationException("Feishu WS endpoint URL is empty.");

        var cfg = data.GetProperty("ClientConfig");
        return new FeishuWsEndpoint(
            Url: url,
            PingIntervalSeconds: cfg.TryGetProperty("PingInterval", out var pi) ? pi.GetInt32() : 90,
            ReconnectCount: cfg.TryGetProperty("ReconnectCount", out var rc) ? rc.GetInt32() : -1,
            ReconnectIntervalSeconds: cfg.TryGetProperty("ReconnectInterval", out var ri) ? ri.GetInt32() : 90,
            ReconnectNonceSeconds: cfg.TryGetProperty("ReconnectNonce", out var rn) ? rn.GetInt32() : 25);
    }

    private HttpRequestMessage BuildSendMessageRequest(
        string accessToken,
        string receiveId,
        string receiveIdType,
        string msgType,
        string content)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/open-apis/im/v1/messages?receive_id_type={receiveIdType}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(
            new
            {
                receive_id = receiveId,
                msg_type = msgType,
                content,
            },
            options: JsonOptions);
        return request;
    }

    private static async Task<string> ParseSendMessageResponse(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var result = await response.Content
            .ReadFromJsonAsync<FeishuSendMessageResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (result is null || result.Code != 0)
        {
            throw new InvalidOperationException(
                $"Feishu send message failed: code={result?.Code}, msg={result?.Msg}");
        }

        return result.Data?.MessageId ?? string.Empty;
    }

    private static string ParseSendMessageResponseFromBytes(byte[] bytes, CancellationToken cancellationToken)
    {
        var result = JsonSerializer.Deserialize<FeishuSendMessageResponse>(bytes, JsonOptions);

        if (result is null || result.Code != 0)
        {
            var responseText = System.Text.Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(
                $"Feishu send message failed: code={result?.Code}, msg={result?.Msg}, body={responseText}");
        }

        return result.Data?.MessageId ?? string.Empty;
    }

    public async Task<Stream> DownloadResourceAsync(
        string accessToken,
        string messageId,
        string fileKey,
        string type,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/open-apis/im/v1/messages/{messageId}/resources/{fileKey}?type={type}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Check if the error response is JSON (API error) or something else
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Feishu download resource failed: status={(int)response.StatusCode}, body={body}");
        }

        // Return a buffered copy of the stream since the HttpClient's internal stream
        // may be invalidated once the response is disposed.
        var ms = new MemoryStream();
        await response.Content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        ms.Position = 0;
        return ms;
    }

}
