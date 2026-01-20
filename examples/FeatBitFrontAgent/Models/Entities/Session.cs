namespace Kode.Agent.WebApiAssistant.Models.Entities;

/// <summary>
/// 会话实体
/// </summary>
public class Session
{
    /// <summary>
    /// 会话唯一标识
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// 关联的用户 ID
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// 会话标题
    /// </summary>
    public string Title { get; set; } = "新对话";

    /// <summary>
    /// 关联的 Agent ID
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 消息数量
    /// </summary>
    public int MessageCount { get; set; } = 0;
}
