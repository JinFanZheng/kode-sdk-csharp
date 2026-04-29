using KodaClaw.Contracts.Media;

namespace KodaClaw.Workspace.Media;

public interface IMediaStore
{
    /// <summary>Saves a media file and returns its metadata.</summary>
    Task<MediaMeta> StoreAsync(string fileName, string contentType, Stream data, CancellationToken cancellationToken = default);

    /// <summary>Saves a media file with provenance metadata and returns its metadata.</summary>
    Task<MediaMeta> StoreAsync(
        Stream data,
        string contentType,
        string? fileName = null,
        string? source = null,
        string? externalMessageId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns metadata for the given media ID, or null if not found.</summary>
    Task<MediaMeta?> GetMetaAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Opens the media file for reading. Returns null if not found.</summary>
    Task<Stream?> OpenReadAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Deletes all unpinned media older than <paramref name="retention"/> and returns cleanup stats.</summary>
    Task<MediaCleanupResult> CleanExpiredAsync(TimeSpan retention, CancellationToken cancellationToken = default);

    /// <summary>Pins a media file so it is exempt from cleanup. Returns false if not found.</summary>
    Task<bool> PinAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Unpins a media file. Returns false if not found.</summary>
    Task<bool> UnpinAsync(string id, CancellationToken cancellationToken = default);
}
