using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KodaClaw.ControlPlane;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKodaClawControlPlane(
        this IServiceCollection services,
        string? workspaceRoot = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICorrelationContextAccessor, AsyncLocalCorrelationContextAccessor>();

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            services.TryAddSingleton<IDiagnosticsService>(_ => new FileDiagnosticsService(workspaceRoot));
        }
        else
        {
            services.TryAddSingleton<IDiagnosticsService, InMemoryDiagnosticsService>();
        }

        return services;
    }
}
