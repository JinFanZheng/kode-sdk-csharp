using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

/// <summary>
/// ChannelAccount 的 JSON 文件存储实现。
/// 路径：{workspaceRoot}/.koda/store/channels/accounts/{id}.json
/// </summary>
public sealed class JsonChannelAccountRepository : JsonStoreBase, IChannelAccountRepository
{
    private readonly string _dir;

    public JsonChannelAccountRepository(string workspaceRoot)
    {
        _dir = Path.Combine(workspaceRoot, ".koda", "store", "channels", "accounts");
    }

    public Task UpsertAsync(ChannelAccount account, CancellationToken cancellationToken = default)
        => WriteEntityAsync(FilePath(account.Id), account, cancellationToken);

    public Task<ChannelAccount?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        => ReadEntityAsync<ChannelAccount>(FilePath(id), cancellationToken);

    public async Task<IReadOnlyList<ChannelAccount>> ListAsync(
        ChannelAccountQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var all = await ScanDirectoryAsync<ChannelAccount>(_dir, null, cancellationToken);

        IEnumerable<ChannelAccount> result = all;

        if (query != null)
        {
            if (query.ConnectorKind.HasValue)
                result = result.Where(a => a.ConnectorKind == query.ConnectorKind.Value);
            if (query.State.HasValue)
                result = result.Where(a => a.State == query.State.Value);
        }

        return result
            .OrderByDescending(a => a.UpdatedAt)
            .ThenByDescending(a => a.CreatedAt)
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
