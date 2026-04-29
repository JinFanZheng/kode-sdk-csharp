using System.Collections.Concurrent;
using KodaClaw.Contracts.Secrets;

namespace KodaClaw.Workspace.Secrets;

public sealed class PlatformSecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, MemorySecretEntry> _memorySecrets = new(StringComparer.Ordinal);
    private readonly IPlatformKeychain _keychain;
    private readonly TimeProvider _timeProvider;

    public PlatformSecretStore(
        IPlatformKeychain keychain,
        TimeProvider? timeProvider = null)
    {
        _keychain = keychain ?? throw new ArgumentNullException(nameof(keychain));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates a <see cref="PlatformSecretStore"/> using the correct OS keychain without DI.
    /// Use this only in bootstrap/static contexts where DI is not yet available.
    /// </summary>
    public static PlatformSecretStore CreateForCurrentPlatform(KodaClawWorkspaceOptions? options = null)
    {
#pragma warning disable CA1416 // each branch is guarded by the OperatingSystem check
        IPlatformKeychain keychain = OperatingSystem.IsWindows()
            ? new WindowsCredentialManager()
            : OperatingSystem.IsMacOS()
                ? new MacOsKeychainCommandRunner()
                : new LinuxSecretStore(options ?? new KodaClawWorkspaceOptions());
#pragma warning restore CA1416
        return new PlatformSecretStore(keychain);
    }

    public async Task<string?> GetAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);

        return NormalizeProvider(secretRef.Provider) switch
        {
            "env" => Normalize(Environment.GetEnvironmentVariable(secretRef.Key)),
            "memory" => _memorySecrets.TryGetValue(secretRef.ToReferenceString(), out var entry)
                ? entry.SecretValue
                : null,
            "keychain" => await _keychain.ReadAsync(secretRef, cancellationToken).ConfigureAwait(false),
            _ => throw CreateProviderNotSupportedException(secretRef),
        };
    }

    public async Task UpsertAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretValue);

        switch (NormalizeProvider(secretRef.Provider))
        {
            case "memory":
                _memorySecrets[secretRef.ToReferenceString()] = new MemorySecretEntry(
                    secretValue.Trim(),
                    _timeProvider.GetUtcNow());
                return;
            case "keychain":
                await _keychain.WriteAsync(secretRef, secretValue.Trim(), cancellationToken).ConfigureAwait(false);
                return;
            case "env":
                throw new InvalidOperationException("Environment-backed secrets are read-only and cannot be upserted by KodaClaw.");
            default:
                throw CreateProviderNotSupportedException(secretRef);
        }
    }

    public async Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);

        switch (NormalizeProvider(secretRef.Provider))
        {
            case "memory":
                _memorySecrets.TryRemove(secretRef.ToReferenceString(), out _);
                return;
            case "keychain":
                await _keychain.DeleteAsync(secretRef, cancellationToken).ConfigureAwait(false);
                return;
            case "env":
                throw new InvalidOperationException("Environment-backed secrets are read-only and cannot be deleted by KodaClaw.");
            default:
                throw CreateProviderNotSupportedException(secretRef);
        }
    }

    public async Task<SecretDescriptor> DescribeAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);

        return NormalizeProvider(secretRef.Provider) switch
        {
            "env" => new SecretDescriptor(
                secretRef,
                Exists: !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(secretRef.Key)),
                IsReadOnly: true,
                StorageDisplayName: "Environment Variable"),
            "memory" => _memorySecrets.TryGetValue(secretRef.ToReferenceString(), out var entry)
                ? new SecretDescriptor(
                    secretRef,
                    Exists: true,
                    IsReadOnly: false,
                    UpdatedAtUtc: entry.UpdatedAtUtc,
                    StorageDisplayName: "In-Memory Secret Store")
                : new SecretDescriptor(
                    secretRef,
                    Exists: false,
                    IsReadOnly: false,
                    StorageDisplayName: "In-Memory Secret Store"),
            "keychain" => new SecretDescriptor(
                secretRef,
                Exists: await _keychain.ExistsAsync(secretRef, cancellationToken).ConfigureAwait(false),
                IsReadOnly: false,
                StorageDisplayName: _keychain.StorageDisplayName),
            _ => throw CreateProviderNotSupportedException(secretRef),
        };
    }

    private static string NormalizeProvider(string provider)
    {
        return provider.Trim().ToLowerInvariant();
    }

    private static string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static InvalidOperationException CreateProviderNotSupportedException(SecretRef secretRef)
    {
        return new InvalidOperationException(
            $"Secret provider '{secretRef.Provider}' is not supported by the current KodaClaw secret store.");
    }

    private sealed record MemorySecretEntry(string SecretValue, DateTimeOffset UpdatedAtUtc);
}
