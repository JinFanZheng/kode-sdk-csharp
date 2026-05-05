using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Canvas;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Models;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.Settings;
using KodaClaw.Contracts.Timers;
using KodaClaw.Storage.Json.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KodaClaw.Storage.Json;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册所有 JSON 文件存储 Repository 实现。
    /// </summary>
    /// <param name="workspaceRoot">Workspace 根目录，通常为 ~/.kodaclaw</param>
    public static IServiceCollection AddKodaClawJsonStore(
        this IServiceCollection services,
        string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        services.TryAddSingleton<ISettingsRepository>(_ => new JsonSettingsRepository(workspaceRoot));
        services.TryAddSingleton<IProviderAccountRepository>(_ => new JsonProviderAccountRepository(workspaceRoot));
        services.TryAddSingleton<IPluginRegistryRepository>(_ => new JsonPluginRegistryRepository(workspaceRoot));
        services.TryAddSingleton<IAutomationDefinitionRepository>(_ => new JsonAutomationDefinitionRepository(workspaceRoot));
        services.TryAddSingleton<IAutomationRunRepository>(_ => new JsonAutomationRunRepository(workspaceRoot));
        services.TryAddSingleton<IAutomationChannelMessageLinkRepository>(_ => new JsonAutomationChannelMessageLinkRepository(workspaceRoot));
        services.TryAddSingleton<IChannelAuditRepository>(_ => new JsonChannelAuditRepository(workspaceRoot));
        services.TryAddSingleton<IPluginLogRepository>(_ => new JsonPluginLogRepository(workspaceRoot));
        services.TryAddSingleton<IInboxRepository>(_ => new JsonInboxRepository(workspaceRoot));
        services.TryAddSingleton<IApprovalRepository>(_ => new JsonApprovalRepository(workspaceRoot));
        services.TryAddSingleton<ICanvasArtifactRepository>(_ => new JsonCanvasArtifactRepository(workspaceRoot));
        services.TryAddSingleton<IChannelAccountRepository>(_ => new JsonChannelAccountRepository(workspaceRoot));
        // ThreadBindingRepository 需要单例保证内存字典唯一
        services.TryAddSingleton<IThreadBindingRepository>(_ => new JsonThreadBindingRepository(workspaceRoot));
        services.TryAddSingleton<IOneShotTimerRepository>(_ => new JsonOneShotTimerRepository(workspaceRoot));

        return services;
    }
}
