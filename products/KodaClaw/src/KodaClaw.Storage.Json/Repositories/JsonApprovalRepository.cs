using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

/// <summary>
/// Approval 的 JSON 文件存储实现。
/// 路径：{workspaceRoot}/.koda/store/approvals/{id}.json
/// </summary>
public sealed class JsonApprovalRepository : JsonStoreBase, IApprovalRepository
{
    private readonly string _dir;

    public JsonApprovalRepository(string workspaceRoot)
    {
        _dir = Path.Combine(workspaceRoot, ".koda", "store", "approvals");
    }

    public Task UpsertAsync(Approval approval, CancellationToken cancellationToken = default)
        => WriteEntityAsync(FilePath(approval.Id), approval, cancellationToken);

    public Task<Approval?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        => ReadEntityAsync<Approval>(FilePath(id), cancellationToken);

    public async Task<IReadOnlyList<Approval>> ListAsync(
        ApprovalQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var all = await ScanDirectoryAsync<Approval>(_dir, null, cancellationToken);

        IEnumerable<Approval> result = all;

        if (query != null)
        {
            if (query.Status.HasValue)
                result = result.Where(a => a.Status == query.Status.Value);
            if (query.Kind.HasValue)
                result = result.Where(a => a.Kind == query.Kind.Value);
            if (query.SessionId != null)
                result = result.Where(a => a.SessionId == query.SessionId);
        }

        return result
            .OrderByDescending(a => a.UpdatedAt)
            .ThenByDescending(a => a.RequestedAt)
            .Take(query?.Limit ?? 50)
            .ToList();
    }

    public async Task<bool> TransitionAsync(
        string id,
        ApprovalStatus status,
        DateTimeOffset updatedAt,
        string? decidedBy = null,
        string? decisionNote = null,
        CancellationToken cancellationToken = default)
    {
        var approval = await ReadEntityAsync<Approval>(FilePath(id), cancellationToken);
        if (approval == null) return false;
        // 只允许从 Pending 状态转换
        if (approval.Status != ApprovalStatus.Pending) return false;

        var updated = approval with
        {
            Status = status,
            UpdatedAt = updatedAt,
            DecidedAt = updatedAt,
            DecidedBy = decidedBy,
            DecisionNote = decisionNote,
        };
        await WriteEntityAsync(FilePath(id), updated, cancellationToken);
        return true;
    }

    private string FilePath(string id) => Path.Combine(_dir, $"{id}.json");
}
