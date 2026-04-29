namespace KodaClaw.Contracts.Canvas;

public interface ICanvasArtifactRepository
{
    Task UpsertAsync(CanvasArtifact artifact, CancellationToken cancellationToken = default);

    Task<CanvasArtifact?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CanvasArtifact>> ListAsync(
        CanvasArtifactQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
