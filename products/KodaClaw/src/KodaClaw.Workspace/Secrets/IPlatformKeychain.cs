using KodaClaw.Contracts.Secrets;

namespace KodaClaw.Workspace.Secrets;

/// <summary>
/// Platform-specific OS credential store backend (macOS Keychain, Windows Credential Manager, Linux Secret Service).
/// </summary>
public interface IPlatformKeychain
{
    /// <summary>Human-readable name shown in the UI, e.g. "macOS Keychain".</summary>
    string StorageDisplayName { get; }

    Task<string?> ReadAsync(SecretRef secretRef, CancellationToken cancellationToken = default);

    Task WriteAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken = default);

    Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(SecretRef secretRef, CancellationToken cancellationToken = default);
}
