using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

/// <summary>
/// ChannelAuditEntry 的 JSONL 文件存储实现（仅追加）。
/// 路径：{workspaceRoot}/.koda/logs/audits/{bindingId}.jsonl
/// </summary>
public sealed class JsonChannelAuditRepository : JsonStoreBase, IChannelAuditRepository
{
    private readonly string _dir;

    public JsonChannelAuditRepository(string workspaceRoot)
    {
        _dir = Path.Combine(workspaceRoot, ".koda", "logs", "audits");
    }

    public Task AppendAsync(ChannelAuditEntry entry, CancellationToken cancellationToken = default)
        => AppendLineAsync(FilePath(entry.BindingId), entry, cancellationToken);

    public async Task<IReadOnlyList<ChannelAuditEntry>> ListByBindingIdAsync(
        string bindingId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        // ReadLastLinesAsync 返回升序（最旧在前）；此处翻转为降序（最新在前），
        // 与 SQLite 的 ORDER BY created_at DESC 行为一致，TryResolveLastTurnOutcome 依赖此顺序。
        var entries = await ReadLastLinesAsync<ChannelAuditEntry>(FilePath(bindingId), limit, cancellationToken);
        entries.Reverse();
        return entries;
    }

    private string FilePath(string bindingId) => Path.Combine(_dir, $"{bindingId}.jsonl");
}
