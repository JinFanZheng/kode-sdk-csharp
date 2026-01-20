using Kode.Agent.WebApiAssistant.Models.Entities;

namespace Kode.Agent.WebApiAssistant.Models.Responses;

/// <summary>
/// 会话响应
/// </summary>
public class SessionResponse
{
    /// <summary>
    /// 会话 ID
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// 用户 ID
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// 会话标题
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Agent ID
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// 消息数量
    /// </summary>
    public int MessageCount { get; set; }

    /// <summary>
    /// 从实体创建响应
    /// </summary>
    public static SessionResponse FromEntity(Session entity)
    {
        return new SessionResponse
        {
            SessionId = entity.SessionId,
            UserId = entity.UserId,
            Title = entity.Title,
            AgentId = entity.AgentId,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            MessageCount = entity.MessageCount
        };
    }
}
