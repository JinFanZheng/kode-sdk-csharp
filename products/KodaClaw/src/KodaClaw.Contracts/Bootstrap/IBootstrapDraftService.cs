namespace KodaClaw.Contracts.Bootstrap;

public interface IBootstrapDraftService
{
    Task<BootstrapDraftResult> GenerateDraftAsync(
        BootstrapDraftRequest request,
        CancellationToken cancellationToken = default);
}
