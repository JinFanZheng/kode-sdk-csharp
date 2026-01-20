using System.Text.Json;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.WebApiAssistant.Models;
using Kode.Agent.WebApiAssistant.Tools.Agent;
using Kode.Agent.WebApiAssistant.Tools.Email;
using Kode.Agent.WebApiAssistant.Tools.Notify;

namespace Kode.Agent.WebApiAssistant.Services;

/// <summary>
/// Per-agent tool loader that reads from agent's .config directory
/// </summary>
public sealed class AgentToolsLoader
{
    private readonly ILogger<AgentToolsLoader> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public AgentToolsLoader(ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
    {
        _logger = loggerFactory.CreateLogger<AgentToolsLoader>();
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Load and register agent-specific tools (email, notify) from agent's .config directory
    /// </summary>
    public async Task LoadAgentToolsAsync(
        string agentId,
        string workDir,
        IToolRegistry toolRegistry,
        CancellationToken cancellationToken = default)
    {
        var agentConfigDir = Path.Combine(workDir, "data", agentId, ".config");

        if (!Directory.Exists(agentConfigDir))
        {
            _logger.LogDebug("Agent config directory not found: {ConfigDir}", agentConfigDir);
            return;
        }

        // Load email tools
        await LoadEmailToolsAsync(agentConfigDir, toolRegistry, cancellationToken);

        // Load notify tools
        await LoadNotifyToolsAsync(agentConfigDir, toolRegistry, cancellationToken);
    }

    /// <summary>
    /// Load email tools from .config/email.json
    /// </summary>
    private async Task LoadEmailToolsAsync(string configDir, IToolRegistry toolRegistry, CancellationToken cancellationToken)
    {
        var emailConfigPath = Path.Combine(configDir, "email.json");
        if (!File.Exists(emailConfigPath))
        {
            _logger.LogDebug("Email config not found: {ConfigPath}", emailConfigPath);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(emailConfigPath, cancellationToken);
            var config = JsonSerializer.Deserialize<EmailConfigFile>(json, _jsonOptions);
            if (config == null)
            {
                _logger.LogWarning("Failed to deserialize email config from {ConfigPath}", emailConfigPath);
                return;
            }

            // Check if config is properly populated
            if (!IsEmailConfigValid(config))
            {
                _logger.LogDebug("Email config exists but not configured: {ConfigPath}", emailConfigPath);
                return;
            }

            _logger.LogInformation("Loading email tools from {ConfigPath}", emailConfigPath);

            var options = ConvertToEmailOptions(config);
            var emailTool = new ImapEmailTool(
                _serviceProvider.GetRequiredService<ILogger<ImapEmailTool>>(),
                options);

            toolRegistry.Register(new EmailListTool(emailTool));
            toolRegistry.Register(new EmailReadTool(emailTool));
            toolRegistry.Register(new EmailDraftTool(emailTool));
            toolRegistry.Register(new EmailMoveTool(emailTool));

            _logger.LogInformation("Registered email tools for agent");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load email tools from {ConfigPath}", emailConfigPath);
        }
    }

    /// <summary>
    /// Load notify tools from .config/notify.json
    /// </summary>
    private async Task LoadNotifyToolsAsync(string configDir, IToolRegistry toolRegistry, CancellationToken cancellationToken)
    {
        var notifyConfigPath = Path.Combine(configDir, "notify.json");
        if (!File.Exists(notifyConfigPath))
        {
            _logger.LogDebug("Notify config not found: {ConfigPath}", notifyConfigPath);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(notifyConfigPath, cancellationToken);
            var config = JsonSerializer.Deserialize<NotifyConfigFile>(json, _jsonOptions);
            if (config == null)
            {
                _logger.LogWarning("Failed to deserialize notify config from {ConfigPath}", notifyConfigPath);
                return;
            }

            // Check if any channel is properly configured
            if (!IsNotifyConfigValid(config))
            {
                _logger.LogDebug("Notify config exists but not configured: {ConfigPath}", notifyConfigPath);
                return;
            }

            _logger.LogInformation("Loading notify tools from {ConfigPath}", notifyConfigPath);

            var options = ConvertToNotifyOptions(config);
            var httpClientFactory = _serviceProvider.GetRequiredService<IHttpClientFactory>();
            var notifyTool = new HttpNotifyTool(
                _serviceProvider.GetRequiredService<ILogger<HttpNotifyTool>>(),
                httpClientFactory,
                options);

            toolRegistry.Register(new NotifySendTool(notifyTool));

            _logger.LogInformation("Registered notify tools for agent");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load notify tools from {ConfigPath}", notifyConfigPath);
        }
    }

    private static bool IsEmailConfigValid(EmailConfigFile config)
    {
        return !string.IsNullOrEmpty(config.Imap?.Host) &&
               !string.IsNullOrEmpty(config.Imap.Auth?.User) &&
               !string.IsNullOrEmpty(config.Imap.Auth?.Pass);
    }

    private static bool IsNotifyConfigValid(NotifyConfigFile config)
    {
        return (config.Channels?.Dingtalk?.Webhook != null && config.Channels.Dingtalk.Webhook.Length > 0) ||
               (config.Channels?.Wecom?.Webhook != null && config.Channels.Wecom.Webhook.Length > 0) ||
               (config.Channels?.Telegram?.BotToken != null && config.Channels.Telegram.BotToken.Length > 0);
    }

    private static EmailOptions ConvertToEmailOptions(EmailConfigFile config)
    {
        return new EmailOptions
        {
            Enabled = true,
            Imap = new EmailServerOptions
            {
                Host = config.Imap!.Host!,
                Port = config.Imap.Port ?? 993,
                UseSsl = config.Imap.Secure ?? true,
                Username = config.Imap.Auth!.User!,
                Password = config.Imap.Auth.Pass!
            },
            Smtp = new EmailServerOptions
            {
                Host = config.Smtp!.Host!,
                Port = config.Smtp.Port ?? 587,
                UseSsl = config.Smtp.Secure ?? false,
                Username = config.Smtp.Auth!.User!,
                Password = config.Smtp.Auth.Pass!
            },
            FromAddress = config.Smtp.Auth.User!,
            FromName = "AI Assistant"
        };
    }

    private static NotifyOptions ConvertToNotifyOptions(NotifyConfigFile config)
    {
        return new NotifyOptions
        {
            DefaultChannel = config.Default ?? "dingtalk",
            DingTalk = new NotifyChannelOptions
            {
                Enabled = !string.IsNullOrEmpty(config.Channels?.Dingtalk?.Webhook),
                WebhookUrl = config.Channels?.Dingtalk?.Webhook ?? "",
                Secret = config.Channels?.Dingtalk?.Secret ?? ""
            },
            WeCom = new NotifyChannelOptions
            {
                Enabled = !string.IsNullOrEmpty(config.Channels?.Wecom?.Webhook),
                WebhookUrl = config.Channels?.Wecom?.Webhook ?? "",
                Secret = ""
            },
            Telegram = new NotifyChannelOptions
            {
                Enabled = !string.IsNullOrEmpty(config.Channels?.Telegram?.BotToken),
                WebhookUrl = $"https://api.telegram.org/bot{config.Channels?.Telegram?.BotToken}/sendMessage",
                Secret = config.Channels?.Telegram?.ChatId ?? ""
            }
        };
    }

    // JSON config file models (matching the structure in InitDefaultConfigs)

    private class EmailConfigFile
    {
        public ImapConfig? Imap { get; set; }
        public SmtpConfig? Smtp { get; set; }
    }

    private class ImapConfig
    {
        public string? Host { get; set; }
        public int? Port { get; set; }
        public bool? Secure { get; set; }
        public AuthConfig? Auth { get; set; }
    }

    private class SmtpConfig
    {
        public string? Host { get; set; }
        public int? Port { get; set; }
        public bool? Secure { get; set; }
        public AuthConfig? Auth { get; set; }
    }

    private class AuthConfig
    {
        public string? User { get; set; }
        public string? Pass { get; set; }
    }

    private class NotifyConfigFile
    {
        public string? Default { get; set; }
        public NotifyChannels? Channels { get; set; }
    }

    private class NotifyChannels
    {
        public DingTalkChannel? Dingtalk { get; set; }
        public WecomChannel? Wecom { get; set; }
        public TelegramChannel? Telegram { get; set; }
    }

    private class DingTalkChannel
    {
        public string? Webhook { get; set; }
        public string? Secret { get; set; }
    }

    private class WecomChannel
    {
        public string? Webhook { get; set; }
    }

    private class TelegramChannel
    {
        public string? BotToken { get; set; }
        public string? ChatId { get; set; }
    }
}
