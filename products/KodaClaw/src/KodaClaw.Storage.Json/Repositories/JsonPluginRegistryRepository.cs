using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

/// <summary>
/// PluginRecord 的 JSON 文件存储实现。
/// 路径：{workspaceRoot}/config/plugins/{id}.json
/// </summary>
public sealed class JsonPluginRegistryRepository : JsonStoreBase, IPluginRegistryRepository
{
    private readonly string _dir;

    public JsonPluginRegistryRepository(string workspaceRoot)
    {
        _dir = Path.Combine(workspaceRoot, "config", "plugins");
    }

    public Task UpsertAsync(PluginRecord record, CancellationToken cancellationToken = default)
        => WriteEntityAsync(FilePath(record.Id), record, cancellationToken);

    public Task<PluginRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        => ReadEntityAsync<PluginRecord>(FilePath(id), cancellationToken);

    public async Task<IReadOnlyList<PluginRecord>> ListAsync(
        PluginQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var all = await ScanDirectoryAsync<PluginRecord>(_dir, null, cancellationToken);

        IEnumerable<PluginRecord> result = all;

        if (query != null)
        {
            if (query.Type.HasValue)
                result = result.Where(p => p.Manifest.Types.Contains(query.Type.Value));
            if (query.TrustState.HasValue)
                result = result.Where(p => p.TrustState == query.TrustState.Value);
            if (query.Enabled.HasValue)
                result = result.Where(p => p.Enabled == query.Enabled.Value);
            if (query.RuntimeState.HasValue)
                result = result.Where(p => p.RuntimeState == query.RuntimeState.Value);
        }

        return result
            .OrderByDescending(p => p.UpdatedAt)
            .Skip(query?.Offset ?? 0)
            .Take(query?.Limit ?? 50)
            .ToList();
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await DeleteEntityAsync(FilePath(id), cancellationToken);
        return true;
    }

    private string FilePath(string id) => Path.Combine(_dir, $"{id}.json");
}
