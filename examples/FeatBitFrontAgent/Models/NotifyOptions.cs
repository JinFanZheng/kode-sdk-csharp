using Microsoft.Extensions.Configuration;

namespace Kode.Agent.WebApiAssistant.Models;

/// <summary>
/// Notification channel configuration
/// </summary>
public class NotifyChannelOptions
{
    public string WebhookUrl { get; set; } = string.Empty;
    public string? Secret { get; set; }
    public bool Enabled { get; set; }
}

/// <summary>
/// Notification options from appsettings.json
/// </summary>
public class NotifyOptions
{
    /// <summary>
    /// DingTalk configuration
    /// </summary>
    public NotifyChannelOptions DingTalk { get; set; } = new();

    /// <summary>
    /// WeCom (WeChat Work) configuration
    /// </summary>
    public NotifyChannelOptions WeCom { get; set; } = new();

    /// <summary>
    /// Telegram configuration
    /// </summary>
    public NotifyChannelOptions Telegram { get; set; } = new();

    /// <summary>
    /// Default channel to use when not specified
    /// </summary>
    public string DefaultChannel { get; set; } = "DingTalk";

    public static NotifyOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Notify");
        var options = new NotifyOptions();

        // DingTalk
        var dingTalkSection = section.GetSection("DingTalk");
        options.DingTalk = new NotifyChannelOptions
        {
            WebhookUrl = dingTalkSection["WebhookUrl"] ?? Environment.GetEnvironmentVariable("DINGTALK_WEBHOOK") ?? "",
            Secret = dingTalkSection["Secret"],
            Enabled = dingTalkSection.GetValue<bool>("Enabled") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DINGTALK_WEBHOOK"))
        };

        // WeCom
        var wecomSection = section.GetSection("WeCom");
        options.WeCom = new NotifyChannelOptions
        {
            WebhookUrl = wecomSection["WebhookUrl"] ?? Environment.GetEnvironmentVariable("WECOM_WEBHOOK") ?? "",
            Secret = wecomSection["Secret"],
            Enabled = wecomSection.GetValue<bool>("Enabled") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WECOM_WEBHOOK"))
        };

        // Telegram
        var telegramSection = section.GetSection("Telegram");
        options.Telegram = new NotifyChannelOptions
        {
            WebhookUrl = telegramSection["WebhookUrl"] ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "",
            Enabled = telegramSection.GetValue<bool>("Enabled") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"))
        };

        options.DefaultChannel = section["DefaultChannel"] ?? "DingTalk";

        return options;
    }

    public NotifyChannelOptions GetChannel(string channelName)
    {
        return channelName.ToLowerInvariant() switch
        {
            "dingtalk" => DingTalk,
            "wecom" => WeCom,
            "wechat" => WeCom,
            "telegram" => Telegram,
            _ => DingTalk
        };
    }
}
