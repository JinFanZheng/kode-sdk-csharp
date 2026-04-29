using KodaClaw.ChannelHub.Audit;
using KodaClaw.Contracts;
using KodaClaw.ChannelHub.Commands;
using KodaClaw.ChannelHub.Connectors.Feishu;
using KodaClaw.ChannelHub.Connectors.Telegram;
using KodaClaw.ChannelHub.Connectors.Webhook;
using KodaClaw.ChannelHub.Connectors.DingTalk;
using KodaClaw.ChannelHub.Connectors.WeChat;
using KodaClaw.ChannelHub.Connectors.Relay;
using KodaClaw.ChannelHub.Delivery;
using KodaClaw.ChannelHub.Inbound;
using KodaClaw.ChannelHub.Policy;
using KodaClaw.ChannelHub.Send;
using KodaClaw.ChannelHub.Turn;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KodaClaw.ChannelHub;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKodaClawChannelHub(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ChannelAuditQueryService>();
        services.TryAddSingleton<ChannelPolicyEngine>();
        services.TryAddSingleton<ChannelEventIngestionService>();
        services.TryAddSingleton<ChannelDeliveryGovernanceService>();
        services.TryAddSingleton<ChannelDeliveryDispatchService>();
        services.TryAddSingleton<ChannelDeliveryApprovalService>();
        services.TryAddSingleton<IChannelThreadSummaryWriter, ChannelThreadSummaryWriter>();
        services.TryAddSingleton<IChannelSendCapture, ChannelSendCapture>();
        services.TryAddSingleton<IChannelSendService, ChannelSendService>();
        services.TryAddSingleton<ChannelCommandDispatcher>();
        services.TryAddSingleton<ChannelTurnOrchestrator>();
        services.TryAddSingleton<ITelegramApiClient, HttpTelegramApiClient>();
        services.TryAddSingleton<TelegramConnector>();
        services.AddSingleton<IChannelConnector>(sp => sp.GetRequiredService<TelegramConnector>());
        services.TryAddSingleton<GenericWebhookConnector>();
        services.AddSingleton<IChannelConnector>(sp => sp.GetRequiredService<GenericWebhookConnector>());
        services.TryAddSingleton<IFeishuApiClient, HttpFeishuApiClient>();
        services.TryAddSingleton<FeishuConnector>();
        services.AddSingleton<IChannelConnector>(sp => sp.GetRequiredService<FeishuConnector>());
        services.TryAddSingleton<IWeChatApiClient, HttpWeChatApiClient>();
        services.TryAddSingleton<IWeChatCdnClient, HttpWeChatCdnClient>();
        services.TryAddSingleton<WeChatAuthManager>();
        services.TryAddSingleton<WeChatConnector>();
        services.AddSingleton<IChannelConnector>(sp => sp.GetRequiredService<WeChatConnector>());
        services.TryAddSingleton<IDingTalkApiClient, HttpDingTalkApiClient>();
        services.TryAddSingleton<DingTalkConnector>();
        services.AddSingleton<IChannelConnector>(sp => sp.GetRequiredService<DingTalkConnector>());
        services.TryAddSingleton<RelayConnector>();
        services.AddSingleton<IChannelConnector>(sp => sp.GetRequiredService<RelayConnector>());
        services.TryAddSingleton<ChannelConnectorKindResolver>();
        services.TryAddSingleton<IAutomationNotificationService, AutomationNotificationService>();
        return services;
    }
}
