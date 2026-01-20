namespace Kode.Agent.WebApiAssistant.Services;

/// <summary>
/// 用户管理服务接口
/// </summary>
public interface IUserService
{
    /// <summary>
    /// 获取用户信息
    /// </summary>
    Task<Models.Entities.User?> GetUserAsync(string userId);

    /// <summary>
    /// 获取或创建用户
    /// </summary>
    Task<Models.Entities.User> GetOrCreateUserAsync(string userId);

    /// <summary>
    /// 获取用户的 Agent ID
    /// </summary>
    Task<string> GetAgentIdAsync(string userId);
}
