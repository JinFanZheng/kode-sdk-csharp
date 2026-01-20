using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.WebApiAssistant.Services;

namespace Kode.Agent.WebApiAssistant.Extensions;

/// <summary>
/// 服务集合扩展
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 添加核心服务
    /// </summary>
    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        // 单例服务
        services.AddSingleton<IUserService, UserService>();
        services.AddSingleton<ISessionService, SessionService>();

        return services;
    }

    /// <summary>
    /// 配置 Skills 系统
    /// </summary>
    public static IServiceCollection AddSkillsSupport(
        this IServiceCollection services,
        Action<SkillsConfig>? configure = null)
    {
        // 配置 SkillsConfig
        var skillsConfig = new SkillsConfig
        {
            Paths = new[] { "Skills" },
            Trusted = Array.Empty<string>()
        };

        configure?.Invoke(skillsConfig);

        services.AddSingleton(skillsConfig);

        return services;
    }
}
