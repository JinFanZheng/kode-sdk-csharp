using KodaClaw.Contracts;
using KodaClaw.Contracts.Canvas;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

/// <summary>
/// CanvasArtifact 的 JSON 文件存储实现。
/// 路径：{workspaceRoot}/.koda/store/canvas/{id}.json
/// </summary>
public sealed class JsonCanvasArtifactRepository : JsonStoreBase, ICanvasArtifactRepository
{
    private readonly string _dir;

    public JsonCanvasArtifactRepository(string workspaceRoot)
    {
        _dir = Path.Combine(workspaceRoot, ".koda", "store", "canvas");
    }

    public Task UpsertAsync(CanvasArtifact artifact, CancellationToken cancellationToken = default)
        => WriteEntityAsync(FilePath(artifact.Id), artifact, cancellationToken);

    public Task<CanvasArtifact?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        => ReadEntityAsync<CanvasArtifact>(FilePath(id), cancellationToken);

    public async Task<IReadOnlyList<CanvasArtifact>> ListAsync(
        CanvasArtifactQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var all = await ScanDirectoryAsync<CanvasArtifact>(_dir, null, cancellationToken);

        IEnumerable<CanvasArtifact> result = all;

        if (query != null)
        {
            if (query.Kind.HasValue)
                result = result.Where(a => a.Kind == query.Kind.Value);
            if (query.Source != null)
                result = result.Where(a => a.Source == query.Source);
            if (query.SessionId != null)
                result = result.Where(a => a.SessionId == query.SessionId);
        }

        return result
            .OrderByDescending(a => a.UpdatedAt)
            .ThenByDescending(a => a.CreatedAt)
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
