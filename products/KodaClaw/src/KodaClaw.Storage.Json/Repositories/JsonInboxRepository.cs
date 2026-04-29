using KodaClaw.Contracts;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

/// <summary>
/// InboxItem 的 JSON 文件存储实现。
/// 路径：{workspaceRoot}/.koda/store/inbox/{id}.json
/// </summary>
public sealed class JsonInboxRepository : JsonStoreBase, IInboxRepository
{
    private readonly string _dir;

    public JsonInboxRepository(string workspaceRoot)
    {
        _dir = Path.Combine(workspaceRoot, ".koda", "store", "inbox");
    }

    public Task UpsertAsync(InboxItem item, CancellationToken cancellationToken = default)
        => WriteEntityAsync(FilePath(item.Id), item, cancellationToken);

    public Task<InboxItem?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        => ReadEntityAsync<InboxItem>(FilePath(id), cancellationToken);

    public async Task<IReadOnlyList<InboxItem>> ListAsync(
        InboxQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var all = await ScanDirectoryAsync<InboxItem>(_dir, null, cancellationToken);

        IEnumerable<InboxItem> result = all;

        if (query != null)
        {
            if (query.Status.HasValue)
                result = result.Where(i => i.Status == query.Status.Value);
            if (query.Kind.HasValue)
                result = result.Where(i => i.Kind == query.Kind.Value);
            if (query.RequiresAction.HasValue)
                result = result.Where(i => i.RequiresAction == query.RequiresAction.Value);
            if (query.SessionId != null)
                result = result.Where(i => i.SessionId == query.SessionId);
        }

        return result
            .OrderByDescending(i => i.UpdatedAt)
            .ThenByDescending(i => i.CreatedAt)
            .Take(query?.Limit ?? 50)
            .ToList();
    }

    public async Task<bool> UpdateStatusAsync(
        string id,
        InboxItemStatus status,
        DateTimeOffset updatedAt,
        DateTimeOffset? resolvedAt = null,
        CancellationToken cancellationToken = default)
    {
        var item = await ReadEntityAsync<InboxItem>(FilePath(id), cancellationToken);
        if (item == null) return false;

        var updated = item with
        {
            Status = status,
            UpdatedAt = updatedAt,
            ResolvedAt = resolvedAt ?? (status >= InboxItemStatus.Resolved ? updatedAt : item.ResolvedAt),
        };
        await WriteEntityAsync(FilePath(id), updated, cancellationToken);
        return true;
    }

    private string FilePath(string id) => Path.Combine(_dir, $"{id}.json");
}
