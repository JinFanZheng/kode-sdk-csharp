namespace Kode.Agent.WebApiAssistant.Services;

/// <summary>
/// 会话管理服务接口
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// 创建新会话
    /// </summary>
    Task<Models.Entities.Session> CreateSessionAsync(string userId, string? title = null);

    /// <summary>
    /// 获取会话详情
    /// </summary>
    Task<Models.Entities.Session?> GetSessionAsync(string sessionId);

    /// <summary>
    /// 列出用户的所有会话
    /// </summary>
    Task<IReadOnlyList<Models.Entities.Session>> ListSessionsAsync(string userId);

    /// <summary>
    /// 删除会话
    /// </summary>
    Task DeleteSessionAsync(string sessionId);

    /// <summary>
    /// 更新会话标题
    /// </summary>
    Task UpdateSessionTitleAsync(string sessionId, string title);

    /// <summary>
    /// 获取或创建用户会话
    /// </summary>
    Task<Models.Entities.Session> GetOrCreateSessionAsync(string userId, string? sessionId = null);
}
