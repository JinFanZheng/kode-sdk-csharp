using KodaClaw.Contracts;

namespace KodaClaw.ChannelHub;

internal sealed class ChannelSecretResolver
{
    private readonly ISecretStore _secretStore;

    public ChannelSecretResolver(ISecretStore? secretStore = null)
    {
        _secretStore = secretStore ?? NullSecretStore.Instance;
    }

    public async Task<string?> ResolveAsync(
        string? explicitSecret,
        string? credentialReferenceFromConfig,
        string? credentialReferenceFromAccount,
        CancellationToken cancellationToken = default)
    {
        var normalizedExplicitSecret = ChannelHubValidation.NormalizeNullableText(explicitSecret);
        if (!string.IsNullOrWhiteSpace(normalizedExplicitSecret))
        {
            return normalizedExplicitSecret;
        }

        var reference = ChannelHubValidation.NormalizeNullableText(credentialReferenceFromConfig)
            ?? ChannelHubValidation.NormalizeNullableText(credentialReferenceFromAccount);
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (SecretRef.TryParse(reference, out var secretRef))
        {
            var resolved = await _secretStore.GetAsync(secretRef, cancellationToken).ConfigureAwait(false);
            return ChannelHubValidation.NormalizeNullableText(resolved);
        }

        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var environmentKey = reference["env:".Length..].Trim();
            if (environmentKey.Length == 0)
            {
                return null;
            }

            return ChannelHubValidation.NormalizeNullableText(Environment.GetEnvironmentVariable(environmentKey));
        }

        if (reference.StartsWith("inline:", StringComparison.OrdinalIgnoreCase))
        {
            return ChannelHubValidation.NormalizeNullableText(reference["inline:".Length..]);
        }

        if (reference.StartsWith("value:", StringComparison.OrdinalIgnoreCase))
        {
            return ChannelHubValidation.NormalizeNullableText(reference["value:".Length..]);
        }

        return ChannelHubValidation.NormalizeNullableText(reference);
    }

    private sealed class NullSecretStore : ISecretStore
    {
        public static NullSecretStore Instance { get; } = new();

        public Task<string?> GetAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task UpsertAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("A writable secret store was not configured.");
        }

        public Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("A writable secret store was not configured.");
        }

        public Task<SecretDescriptor> DescribeAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SecretDescriptor(
                secretRef,
                Exists: false,
                IsReadOnly: true,
                StorageDisplayName: "Unavailable Secret Store"));
        }
    }
}
