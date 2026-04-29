using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

/// <summary>
/// AutomationRunRecord 的 JSON 文件存储实现。
/// 路径：{workspaceRoot}/.koda/store/runs/{runId}.json（每条一文件，WAL）
/// </summary>
public sealed class JsonAutomationRunRepository : JsonStoreBase, IAutomationRunRepository
{
    private readonly string _dir;

    public JsonAutomationRunRepository(string workspaceRoot)
    {
        _dir = Path.Combine(workspaceRoot, ".koda", "store", "runs");
    }

    public Task AddAsync(AutomationRunRecord runRecord, CancellationToken cancellationToken = default)
        => WriteEntityAsync(FilePath(runRecord.RunId), runRecord, cancellationToken);

    public async Task<bool> UpdateAsync(AutomationRunRecord runRecord, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath(runRecord.RunId))) return false;
        await WriteEntityAsync(FilePath(runRecord.RunId), runRecord, cancellationToken);
        return true;
    }

    public Task<AutomationRunRecord?> GetByIdAsync(string runId, CancellationToken cancellationToken = default)
        => ReadEntityAsync<AutomationRunRecord>(FilePath(runId), cancellationToken);

    public async Task<IReadOnlyList<AutomationRunRecord>> ListAsync(
        AutomationRunQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var all = await ScanDirectoryAsync<AutomationRunRecord>(_dir, null, cancellationToken);

        IEnumerable<AutomationRunRecord> result = all;

        if (query != null)
        {
            if (query.AutomationId != null)
                result = result.Where(r => r.AutomationId == query.AutomationId);
            if (query.Status.HasValue)
                result = result.Where(r => r.Status == query.Status.Value);
        }

        return result
            .OrderByDescending(r => r.StartedAt)
            .Take(query?.Limit ?? 50)
            .ToList();
    }

    private string FilePath(string runId) => Path.Combine(_dir, $"{runId}.json");
}
