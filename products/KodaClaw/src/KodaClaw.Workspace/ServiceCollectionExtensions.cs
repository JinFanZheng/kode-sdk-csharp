using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Bootstrap;
using KodaClaw.Contracts.Memory;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Workspace.Git;
using KodaClaw.Workspace.Heartbeat;
using KodaClaw.Workspace.Media;
using KodaClaw.Workspace.Memory;
using KodaClaw.Workspace.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Workspace;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKodaClawWorkspace(
        this IServiceCollection services,
        Action<KodaClawWorkspaceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new KodaClawWorkspaceOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        RegisterPlatformKeychain(services);
        services.TryAddSingleton<ISecretStore, PlatformSecretStore>();
        services.TryAddSingleton<IWorkspaceService, WorkspaceService>();
        services.TryAddSingleton<IWorkspaceGitService, WorkspaceGitService>();
        services.TryAddSingleton<IMediaStore, LocalMediaStore>();
        services.TryAddSingleton<IWorkspaceReadinessService, WorkspaceReadinessService>();
        services.TryAddSingleton<IBootstrapService, BootstrapService>();
        services.TryAddSingleton<IMemoryFileService, MemoryFileService>();
        services.TryAddSingleton<IHeartbeatAutomationCompiler, HeartbeatAutomationCompiler>();
        services.TryAddSingleton<IHeartbeatSyncService>(provider => new HeartbeatSyncService(
            provider.GetRequiredService<IWorkspaceService>(),
            provider.GetRequiredService<IHeartbeatAutomationCompiler>(),
            provider.GetRequiredService<IAutomationDefinitionRepository>(),
            provider.GetService<ILogger<HeartbeatSyncService>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HeartbeatFileWatcherHostedService>());
        return services;
    }

    private static void RegisterPlatformKeychain(IServiceCollection services)
    {
        // Each branch is guarded by the matching OperatingSystem.Is*() check, so the
        // platform-specific types are only instantiated on the correct OS.
#pragma warning disable CA1416
        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<IPlatformKeychain, WindowsCredentialManager>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.TryAddSingleton<IPlatformKeychain, MacOsKeychainCommandRunner>();
        }
        else
        {
            // Linux (or any other Unix): secret-tool with file fallback
            services.TryAddSingleton<IPlatformKeychain, LinuxSecretStore>();
        }
#pragma warning restore CA1416
    }
}
