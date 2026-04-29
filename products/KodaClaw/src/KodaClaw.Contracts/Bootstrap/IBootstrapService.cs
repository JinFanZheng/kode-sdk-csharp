namespace KodaClaw.Contracts.Bootstrap;

public interface IBootstrapService
{
    Task<BootstrapCompletionResult> CompleteAsync(
        BootstrapCompletionRequest request,
        CancellationToken cancellationToken = default);
}
