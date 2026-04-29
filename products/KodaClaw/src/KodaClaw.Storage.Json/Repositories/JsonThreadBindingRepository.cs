using System.Collections.Concurrent;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

/// <summary>
/// ThreadBinding 的 JSON 文件存储实现，含内存字典热路径。
/// 路径：{workspaceRoot}/.koda/store/channels/bindings/{id}.json
///
/// GetByExternalThreadAsync 是渠道消息的热路径，通过 ConcurrentDictionary 实现 O(1) 查找。
/// 启动时（首次访问）懒加载全部 binding 到内存字典。
/// </summary>
public sealed class JsonThreadBindingRepository : JsonStoreBase, IThreadBindingRepository
{
    private readonly string _dir;

    // (connectorKind, accountId, externalThreadId) → bindingId
    private readonly ConcurrentDictionary<(string, string, string), string> _index = new();
    private volatile bool _loaded;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public JsonThreadBindingRepository(string workspaceRoot)
    {
        _dir = Path.Combine(workspaceRoot, ".koda", "store", "channels", "bindings");
    }

    public async Task UpsertAsync(ThreadBinding binding, CancellationToken cancellationToken = default)
    {
        await WriteEntityAsync(FilePath(binding.Id), binding, cancellationToken);
        // 写入成功后更新内存字典
        _index[IndexKey(binding)] = binding.Id;
    }

    public async Task<ThreadBinding?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return await ReadEntityAsync<ThreadBinding>(FilePath(id), cancellationToken);
    }

    /// <summary>热路径：O(1) 字典查找，再读单文件。</summary>
    public async Task<ThreadBinding?> GetByExternalThreadAsync(
        ChannelConnectorKind connectorKind,
        string accountId,
        string externalThreadId,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var key = (connectorKind.ToString(), accountId, externalThreadId);
        if (!_index.TryGetValue(key, out var bindingId)) return null;
        return await ReadEntityAsync<ThreadBinding>(FilePath(bindingId), cancellationToken);
    }

    public async Task<IReadOnlyList<ThreadBinding>> ListAsync(
        ChannelQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var all = await ScanDirectoryAsync<ThreadBinding>(_dir, null, cancellationToken);

        IEnumerable<ThreadBinding> result = all;

        if (query != null)
        {
            if (query.ConnectorKind.HasValue)
                result = result.Where(b => b.ConnectorKind == query.ConnectorKind.Value);
            if (query.AccountId != null)
                result = result.Where(b => b.AccountId == query.AccountId);
            if (query.ThreadType.HasValue)
                result = result.Where(b => b.ThreadType == query.ThreadType.Value);
            if (query.SessionKind.HasValue)
                result = result.Where(b => b.SessionKind == query.SessionKind.Value);
            if (query.SessionId != null)
                result = result.Where(b => b.SessionId == query.SessionId);
        }

        return result
            .OrderByDescending(b => b.UpdatedAt)
            .ThenByDescending(b => b.CreatedAt)
            .Take(query?.Limit ?? 50)
            .ToList();
    }

    public async Task<ThreadBinding?> GetBySessionIdAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var results = await ListAsync(
            new ChannelQuery(SessionId: sessionId, Limit: 1),
            cancellationToken);
        return results.FirstOrDefault();
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var binding = await ReadEntityAsync<ThreadBinding>(FilePath(id), cancellationToken);
        if (binding != null)
            _index.TryRemove(IndexKey(binding), out _);

        await DeleteEntityAsync(FilePath(id), cancellationToken);
        return true;
    }

    public async Task<int> DeleteByAccountIdAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var bindingIds = _index
            .Where(kv => kv.Key.Item2 == accountId)
            .Select(kv => kv.Value)
            .ToList();

        foreach (var bindingId in bindingIds)
            await DeleteAsync(bindingId, cancellationToken);

        return bindingIds.Count;
    }

    public async Task<int> UpdateDeliveryModeByAccountIdAsync(string accountId, DeliveryMode mode, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var bindingIds = _index
            .Where(kv => kv.Key.Item2 == accountId)
            .Select(kv => kv.Value)
            .ToList();

        foreach (var bindingId in bindingIds)
            await UpdateDeliveryModeOverrideAsync(bindingId, mode, cancellationToken);

        return bindingIds.Count;
    }

    public async Task<bool> UpdateDeliveryModeOverrideAsync(
        string id,
        DeliveryMode? deliveryModeOverride,
        CancellationToken cancellationToken = default)
    {
        var binding = await ReadEntityAsync<ThreadBinding>(FilePath(id), cancellationToken);
        if (binding == null) return false;

        var updated = binding with { DeliveryModeOverride = deliveryModeOverride };
        await WriteEntityAsync(FilePath(id), updated, cancellationToken);
        return true;
    }

    // ─── 私有工具 ──────────────────────────────────────────────────────────────

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            var all = await ScanDirectoryAsync<ThreadBinding>(_dir, null, ct);
            foreach (var b in all)
                _index[IndexKey(b)] = b.Id;
            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static (string, string, string) IndexKey(ThreadBinding b)
        => (b.ConnectorKind.ToString(), b.AccountId, b.ExternalThreadId);

    private string FilePath(string id) => Path.Combine(_dir, $"{id}.json");
}
