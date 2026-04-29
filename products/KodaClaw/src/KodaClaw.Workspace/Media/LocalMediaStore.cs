using System.Text.Json;
using KodaClaw.Contracts.Media;
using KodaClaw.Contracts.Workspace;

namespace KodaClaw.Workspace.Media;

/// <summary>
/// File-system-backed media store under <c>{workspace}/media/</c>.
/// Each media object is stored as two files:
/// <list type="bullet">
///   <item><c>{id}.bin</c> — raw binary data</item>
///   <item><c>{id}.meta.json</c> — serialized <see cref="MediaMeta"/></item>
/// </list>
/// </summary>
public sealed class LocalMediaStore : IMediaStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _mediaDir;

    public LocalMediaStore(KodaClawWorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _mediaDir = Path.Combine(options.ResolveRootPath(), KodaClawWorkspaceLayout.MediaDirectory);
    }

    public async Task<MediaMeta> StoreAsync(
        string fileName,
        string contentType,
        Stream data,
        CancellationToken cancellationToken = default)
    {
        return await StoreAsync(data, contentType, fileName, source: null, externalMessageId: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MediaMeta> StoreAsync(
        Stream data,
        string contentType,
        string? fileName = null,
        string? source = null,
        string? externalMessageId = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_mediaDir);

        const long MaxSizeBytes = 50 * 1024 * 1024; // 50MB
        if (data.CanSeek && data.Length > MaxSizeBytes)
        {
            throw new ArgumentException(
                $"Media file too large: {data.Length:N0} bytes exceeds limit of {MaxSizeBytes:N0} bytes.",
                nameof(data));
        }

        var id = Guid.NewGuid().ToString("N");
        var binPath = Path.Combine(_mediaDir, $"{id}.bin");
        var metaPath = Path.Combine(_mediaDir, $"{id}.meta.json");

        long size;
        await using (var fs = File.Create(binPath))
        {
            await data.CopyToAsync(fs, cancellationToken);
            size = fs.Length;
        }

        var meta = new MediaMeta(
            Id: id,
            FileName: fileName ?? "unnamed",
            ContentType: contentType,
            SizeBytes: size,
            StoredAt: DateTimeOffset.UtcNow,
            Source: source,
            ExternalMessageId: externalMessageId);

        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, JsonOptions), cancellationToken);
        return meta;
    }


    public async Task<MediaMeta?> GetMetaAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsValidId(id)) return null;
        var metaPath = Path.Combine(_mediaDir, $"{id}.meta.json");
        if (!File.Exists(metaPath)) return null;
        var json = await File.ReadAllTextAsync(metaPath, cancellationToken);
        return JsonSerializer.Deserialize<MediaMeta>(json);
    }

    public Task<Stream?> OpenReadAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsValidId(id)) return Task.FromResult<Stream?>(null);
        var binPath = Path.Combine(_mediaDir, $"{id}.bin");
        if (!File.Exists(binPath)) return Task.FromResult<Stream?>(null);
        Stream stream = File.OpenRead(binPath);
        return Task.FromResult<Stream?>(stream);
    }

    public async Task<bool> PinAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsValidId(id)) return false;
        var metaPath = Path.Combine(_mediaDir, $"{id}.meta.json");
        if (!File.Exists(metaPath)) return false;
        var json = await File.ReadAllTextAsync(metaPath, cancellationToken);
        var meta = JsonSerializer.Deserialize<MediaMeta>(json, JsonOptions);
        if (meta is null) return false;
        var updated = meta with { IsPinned = true };
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(updated, JsonOptions), cancellationToken);
        return true;
    }

    public async Task<bool> UnpinAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsValidId(id)) return false;
        var metaPath = Path.Combine(_mediaDir, $"{id}.meta.json");
        if (!File.Exists(metaPath)) return false;
        var json = await File.ReadAllTextAsync(metaPath, cancellationToken);
        var meta = JsonSerializer.Deserialize<MediaMeta>(json, JsonOptions);
        if (meta is null) return false;
        var updated = meta with { IsPinned = false };
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(updated, JsonOptions), cancellationToken);
        return true;
    }

    public async Task<MediaCleanupResult> CleanExpiredAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_mediaDir))
            return new MediaCleanupResult(0, 0, 0);

        var cutoff = DateTimeOffset.UtcNow - retention;
        var deletedCount = 0;
        var freedBytes = 0L;
        var pinnedCount = 0;

        foreach (var metaPath in Directory.EnumerateFiles(_mediaDir, "*.meta.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(metaPath, cancellationToken);
                var meta = JsonSerializer.Deserialize<MediaMeta>(json, JsonOptions);
                if (meta is null) continue;

                if (meta.IsPinned)
                {
                    pinnedCount++;
                    continue;
                }

                if (meta.StoredAt < cutoff)
                {
                    var binPath = Path.Combine(_mediaDir, $"{meta.Id}.bin");
                    if (File.Exists(binPath))
                    {
                        freedBytes += new FileInfo(binPath).Length;
                        File.Delete(binPath);
                    }
                    File.Delete(metaPath);
                    deletedCount++;
                }
            }
            catch (Exception)
            {
                // Skip corrupt or inaccessible meta files without interrupting cleanup.
            }
        }

        return new MediaCleanupResult(deletedCount, freedBytes, pinnedCount);
    }

    private static bool IsValidId(string id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.Length <= 64 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
}
