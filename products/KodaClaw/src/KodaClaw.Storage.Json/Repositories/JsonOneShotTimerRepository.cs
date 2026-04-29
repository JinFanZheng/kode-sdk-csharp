using KodaClaw.Contracts;
using KodaClaw.Contracts.Timers;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

/// <summary>
/// 一次性定时提醒的 JSON 文件存储实现。
/// 路径：{workspaceRoot}/.one-shot-timers.json
/// 所有 timer 存储在单一 JSON 数组文件中，WAL 原子写保证一致性。
/// </summary>
public sealed class JsonOneShotTimerRepository : JsonStoreBase, IOneShotTimerRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonOneShotTimerRepository(string workspaceRoot)
    {
        _filePath = Path.Combine(workspaceRoot, ".one-shot-timers.json");
    }

    public async Task<OneShotTimerRecord?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var all = await ReadAllAsync(cancellationToken);
        return all.FirstOrDefault(t => t.Id == id);
    }

    public async Task<IReadOnlyList<OneShotTimerRecord>> ListPendingAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var all = await ReadAllAsync(cancellationToken);
        return all
            .Where(t => t.Status == OneShotTimerStatus.Pending && t.FireAt <= now)
            .OrderBy(t => t.FireAt)
            .ToList();
    }

    public async Task AddAsync(
        OneShotTimerRecord record,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadAllAsync(cancellationToken);
            var updated = all.Append(record).ToList();
            await WriteEntityAsync(_filePath, updated, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> UpdateAsync(
        OneShotTimerRecord record,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadAllAsync(cancellationToken);
            var index = all.FindIndex(t => t.Id == record.Id);
            if (index < 0) return false;
            all[index] = record;
            await WriteEntityAsync(_filePath, all, cancellationToken);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<OneShotTimerRecord>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var list = await ReadEntityAsync<List<OneShotTimerRecord>>(_filePath, cancellationToken);
        return list ?? [];
    }
}
