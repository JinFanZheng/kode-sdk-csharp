using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

/// <summary>
/// AutomationDefinition 的 JSON 文件存储实现。
/// 路径：{workspaceRoot}/.koda/store/automations/{id}.json
/// </summary>
public sealed class JsonAutomationDefinitionRepository : JsonStoreBase, IAutomationDefinitionRepository
{
    private readonly string _dir;

    public JsonAutomationDefinitionRepository(string workspaceRoot)
    {
        _dir = Path.Combine(workspaceRoot, ".koda", "store", "automations");
    }

    public Task UpsertAsync(AutomationDefinition definition, CancellationToken cancellationToken = default)
        => WriteEntityAsync(FilePath(definition.Id), definition, cancellationToken);

    public Task<AutomationDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        => ReadEntityAsync<AutomationDefinition>(FilePath(id), cancellationToken);

    public async Task<IReadOnlyList<AutomationDefinition>> ListAsync(
        AutomationDefinitionQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var all = await ScanDirectoryAsync<AutomationDefinition>(_dir, null, cancellationToken);

        IEnumerable<AutomationDefinition> result = all;

        if (query != null)
        {
            if (query.Enabled.HasValue)
                result = result.Where(d => d.Enabled == query.Enabled.Value);
            if (query.Source.HasValue)
                result = result.Where(d => d.Source == query.Source.Value);
        }

        return result
            .OrderByDescending(d => d.UpdatedAt)
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
