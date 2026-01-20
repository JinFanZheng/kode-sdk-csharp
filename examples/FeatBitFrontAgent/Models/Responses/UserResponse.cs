using Kode.Agent.WebApiAssistant.Models.Entities;

namespace Kode.Agent.WebApiAssistant.Models.Responses;

/// <summary>
/// 用户响应
/// </summary>
public class UserResponse
{
    /// <summary>
    /// 用户 ID
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Agent ID
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 最后活跃时间
    /// </summary>
    public DateTime LastActiveAt { get; set; }

    /// <summary>
    /// 从实体创建响应
    /// </summary>
    public static UserResponse FromEntity(User entity)
    {
        return new UserResponse
        {
            UserId = entity.UserId,
            DisplayName = entity.DisplayName,
            AgentId = entity.AgentId,
            CreatedAt = entity.CreatedAt,
            LastActiveAt = entity.LastActiveAt
        };
    }
}
