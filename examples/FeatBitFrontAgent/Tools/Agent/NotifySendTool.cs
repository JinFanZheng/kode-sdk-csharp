using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.WebApiAssistant.Tools.Notify;

namespace Kode.Agent.WebApiAssistant.Tools.Agent;

/// <summary>
/// Tool for sending notifications.
/// </summary>
[Tool("notify_send")]
public sealed class NotifySendTool : ToolBase<NotifySendArgs>
{
    private readonly INotifyTool _notifyTool;

    public NotifySendTool(INotifyTool notifyTool)
    {
        _notifyTool = notifyTool;
    }

    public override string Name => "notify_send";

    public override string Description =>
        "Send a notification to external channels like DingTalk, WeCom, or Telegram. " +
        "Use this to alert users about important events or results.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<NotifySendArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = false
    };

    protected override async Task<ToolResult> ExecuteAsync(
        NotifySendArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var priority = Enum.Parse<NotificationPriority>(args.Priority ?? "Normal", true);
            var channel = Enum.Parse<NotificationChannel>(args.Channel ?? "DingTalk", true);

            var success = await _notifyTool.SendNotificationAsync(
                args.Title,
                args.Content,
                priority,
                channel);

            if (success)
            {
                return ToolResult.Ok(new
                {
                    sent = true,
                    channel = channel.ToString(),
                    title = args.Title,
                    message = "Notification sent successfully"
                });
            }
            else
            {
                return ToolResult.Ok(new
                {
                    sent = false,
                    channel = channel.ToString(),
                    warning = "Notification was not sent (channel may not be configured)"
                });
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to send notification: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for notify_send tool.
/// </summary>
[GenerateToolSchema]
public class NotifySendArgs
{
    [ToolParameter(Description = "Notification title")]
    public required string Title { get; init; }

    [ToolParameter(Description = "Notification content/message")]
    public required string Content { get; init; }

    [ToolParameter(Description = "Priority level: Low, Normal, High", Required = false)]
    public string? Priority { get; init; }

    [ToolParameter(Description = "Channel: DingTalk, WeCom, Telegram", Required = false)]
    public string? Channel { get; init; }
}
