namespace KodaClaw.Contracts.Plugins;

public interface IPluginLogRepository
{
    Task AppendAsync(PluginLogEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PluginLogEntry>> ListAsync(
        string pluginId,
        int limit = 200,
        CancellationToken cancellationToken = default);
}
