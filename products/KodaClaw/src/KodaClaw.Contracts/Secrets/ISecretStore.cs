namespace KodaClaw.Contracts.Secrets;

public interface ISecretStore
{
    Task<string?> GetAsync(SecretRef secretRef, CancellationToken cancellationToken = default);

    Task UpsertAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken = default);

    Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default);

    Task<SecretDescriptor> DescribeAsync(SecretRef secretRef, CancellationToken cancellationToken = default);
}
