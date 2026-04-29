namespace KodaClaw.Contracts.Plugins;

public interface IPluginRegistryRepository
{
    Task UpsertAsync(PluginRecord record, CancellationToken cancellationToken = default);

    Task<PluginRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PluginRecord>> ListAsync(
        PluginQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
