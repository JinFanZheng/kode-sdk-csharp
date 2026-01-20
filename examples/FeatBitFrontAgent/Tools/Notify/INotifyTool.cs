namespace Kode.Agent.WebApiAssistant.Tools.Notify;

/// <summary>
/// 通知优先级
/// </summary>
public enum NotificationPriority
{
    Low,
    Normal,
    High
}

/// <summary>
/// 通知渠道
/// </summary>
public enum NotificationChannel
{
    DingTalk,
    WeCom,
    Telegram
}

/// <summary>
/// 通知工具接口
/// </summary>
public interface INotifyTool
{
    /// <summary>
    /// 发送通知
    /// </summary>
    Task<bool> SendNotificationAsync(
        string title,
        string content,
        NotificationPriority priority = NotificationPriority.Normal,
        NotificationChannel channel = NotificationChannel.DingTalk);

    /// <summary>
    /// 检查通知配置
    /// </summary>
    Task<bool> IsConfiguredAsync(NotificationChannel channel);
}
