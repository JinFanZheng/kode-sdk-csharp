using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using Kode.Agent.Sdk.Core.Abstractions;

namespace KodaClaw.PluginHost.Hosting;

public interface IPluginLifecycleHost
{
    Task<PluginRecord> StartAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<PluginRecord> StopAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<PluginRecord> RestartAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<PluginRecord> CheckHealthAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ITool>> GetToolsAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ITool>> ListToolsAsync(CancellationToken cancellationToken = default);
}
