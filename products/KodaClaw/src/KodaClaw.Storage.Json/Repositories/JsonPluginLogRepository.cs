using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

/// <summary>
/// PluginLogEntry 的 JSONL 文件存储实现（仅追加，5000 行触发截断保留 2000）。
/// 路径：{workspaceRoot}/.koda/logs/plugins/{pluginId}.jsonl
/// </summary>
public sealed class JsonPluginLogRepository : JsonStoreBase, IPluginLogRepository
{
    private const int MaxLines = 5_000;
    private const int KeepLines = 2_000;
    private const int DedupWindow = 50;

    private readonly string _dir;

    public JsonPluginLogRepository(string workspaceRoot)
    {
        _dir = Path.Combine(workspaceRoot, ".koda", "logs", "plugins");
    }

    public async Task AppendAsync(PluginLogEntry entry, CancellationToken cancellationToken = default)
    {
        var path = FilePath(entry.PluginId);

        // 幂等去重：检查最近 DedupWindow 行是否已有相同 entryId
        if (await IsDuplicateAsync(path, entry.EntryId, cancellationToken))
            return;

        await AppendLineAsync(path, entry, cancellationToken);
        await TrimLogFileAsync(path, MaxLines, KeepLines, cancellationToken);
    }

    public async Task<IReadOnlyList<PluginLogEntry>> ListAsync(
        string pluginId,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadLastLinesAsync<PluginLogEntry>(FilePath(pluginId), limit, cancellationToken);
        return result;
    }

    // ─── 私有工具 ──────────────────────────────────────────────────────────────

    private async Task<bool> IsDuplicateAsync(string path, string entryId, CancellationToken ct)
    {
        var recent = await ReadLastLinesAsync<PluginLogEntry>(path, DedupWindow, ct);
        return recent.Any(e => e.EntryId == entryId);
    }

    private string FilePath(string pluginId) => Path.Combine(_dir, $"{pluginId}.jsonl");
}
