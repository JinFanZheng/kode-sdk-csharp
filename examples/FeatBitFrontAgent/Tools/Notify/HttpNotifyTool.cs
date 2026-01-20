using System.Security.Cryptography;
using System.Text;
using Kode.Agent.WebApiAssistant.Models;

namespace Kode.Agent.WebApiAssistant.Tools.Notify;

/// <summary>
/// HTTP 通知工具实现
/// </summary>
public class HttpNotifyTool : INotifyTool
{
    private readonly ILogger<HttpNotifyTool> _logger;
    private readonly HttpClient _httpClient;
    private readonly NotifyOptions _options;

    public HttpNotifyTool(ILogger<HttpNotifyTool> logger, IHttpClientFactory httpClientFactory, NotifyOptions options)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("Notify");
        _options = options;
    }

    public async Task<bool> SendNotificationAsync(
        string title,
        string content,
        NotificationPriority priority = NotificationPriority.Normal,
        NotificationChannel channel = NotificationChannel.DingTalk)
    {
        _logger.LogInformation("Sending notification via {Channel}: {Title}", channel, title);

        try
        {
            var channelConfig = _options.GetChannel(channel.ToString());
            if (!channelConfig.Enabled || string.IsNullOrEmpty(channelConfig.WebhookUrl))
            {
                _logger.LogWarning("Channel {Channel} is not configured or disabled", channel);
                return false;
            }

            var webhookUrl = channelConfig.WebhookUrl;

            // For DingTalk, add signature if secret is configured
            if (channel == NotificationChannel.DingTalk && !string.IsNullOrEmpty(channelConfig.Secret))
            {
                webhookUrl = AddDingTalkSignature(webhookUrl, channelConfig.Secret);
            }

            // For Telegram, add chat_id to URL if configured in Secret
            if (channel == NotificationChannel.Telegram && !string.IsNullOrEmpty(channelConfig.Secret))
            {
                var separator = webhookUrl.Contains('?') ? '&' : '?';
                webhookUrl = $"{webhookUrl}{separator}chat_id={Uri.EscapeDataString(channelConfig.Secret)}";
            }

            // Build payload based on channel type
            object payload = channel switch
            {
                NotificationChannel.DingTalk => BuildDingTalkPayload(title, content, priority),
                NotificationChannel.WeCom => BuildWeComPayload(title, content, priority),
                NotificationChannel.Telegram => BuildTelegramPayload(title, content, priority),
                _ => new { }
            };

            var response = await _httpClient.PostAsJsonAsync(webhookUrl, payload);
            var result = response.IsSuccessStatusCode;

            if (result)
            {
                _logger.LogInformation("Notification sent successfully via {Channel}", channel);
            }
            else
            {
                _logger.LogWarning("Failed to send notification via {Channel}: {StatusCode}",
                    channel, response.StatusCode);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send notification via {Channel}", channel);
            return false;
        }
    }

    /// <summary>
    /// Add DingTalk signature to webhook URL
    /// </summary>
    private static string AddDingTalkSignature(string webhookUrl, string secret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stringToSign = $"{timestamp}\n{secret}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        var sign = Convert.ToBase64String(hash);

        var separator = webhookUrl.Contains('?') ? '&' : '?';
        return $"{webhookUrl}{separator}timestamp={timestamp}&sign={Uri.EscapeDataString(sign)}";
    }

    public Task<bool> IsConfiguredAsync(NotificationChannel channel)
    {
        var channelConfig = _options.GetChannel(channel.ToString());
        return Task.FromResult(channelConfig.Enabled && !string.IsNullOrEmpty(channelConfig.WebhookUrl));
    }

    private static object BuildDingTalkPayload(string title, string content, NotificationPriority priority)
    {
        // Add priority indicator
        var priorityText = priority switch
        {
            NotificationPriority.High => "### ⚠️ 紧急\n",
            NotificationPriority.Low => "### ℹ️ 提醒\n",
            _ => "### 📢 通知\n"
        };

        // Markdown format
        var markdownContent = $"{priorityText}**{title}**\n\n{content}";

        return new
        {
            msgtype = "markdown",
            markdown = new
            {
                title = title,
                text = markdownContent
            }
        };
    }

    private static object BuildWeComPayload(string title, string content, NotificationPriority priority)
    {
        return new
        {
            msgtype = "text",
            text = new
            {
                content = $"[{title}] {content}"
            }
        };
    }

    private static object BuildTelegramPayload(string title, string content, NotificationPriority priority)
    {
        return new
        {
            text = $"[{title}] {content}",
            parse_mode = "HTML"
        };
    }
}
