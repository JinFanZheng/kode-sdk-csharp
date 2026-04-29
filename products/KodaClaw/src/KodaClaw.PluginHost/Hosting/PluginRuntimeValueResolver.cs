using KodaClaw.Contracts;
using KodaClaw.Contracts.Secrets;

namespace KodaClaw.PluginHost.Hosting;

internal sealed class PluginRuntimeValueResolver
{
    private readonly ISecretStore _secretStore;

    public PluginRuntimeValueResolver(ISecretStore? secretStore = null)
    {
        _secretStore = secretStore ?? NullSecretStore.Instance;
    }

    public IReadOnlyDictionary<string, string>? Resolve(
        IReadOnlyDictionary<string, string>? literalValues,
        IReadOnlyDictionary<string, string>? referenceValues)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (literalValues is not null)
        {
            foreach (var (rawKey, rawValue) in literalValues)
            {
                var key = NormalizeNullable(rawKey);
                var value = NormalizeNullable(rawValue);
                if (key is null || value is null)
                {
                    continue;
                }

                resolved[key] = value;
            }
        }

        if (referenceValues is not null)
        {
            foreach (var (rawKey, rawReference) in referenceValues)
            {
                var key = NormalizeNullable(rawKey);
                if (key is null)
                {
                    continue;
                }

                var value = ResolveReference(rawReference);
                if (value is null)
                {
                    continue;
                }

                resolved[key] = value;
            }
        }

        return resolved.Count == 0 ? null : resolved;
    }

    private string? ResolveReference(string? rawReference)
    {
        var reference = NormalizeNullable(rawReference);
        if (reference is null)
        {
            return null;
        }

        if (SecretRef.TryParse(reference, out var secretRef))
        {
            var secretValue = _secretStore.GetAsync(secretRef).GetAwaiter().GetResult();
            return NormalizeNullable(secretValue);
        }

        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var environmentKey = NormalizeNullable(reference["env:".Length..]);
            if (environmentKey is null)
            {
                return null;
            }

            return NormalizeNullable(Environment.GetEnvironmentVariable(environmentKey));
        }

        if (reference.StartsWith("inline:", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeNullable(reference["inline:".Length..]);
        }

        if (reference.StartsWith("value:", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeNullable(reference["value:".Length..]);
        }

        return reference;
    }

    private static string? NormalizeNullable(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
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
