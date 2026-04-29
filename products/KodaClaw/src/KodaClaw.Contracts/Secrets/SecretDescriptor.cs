namespace KodaClaw.Contracts.Secrets;

public sealed record SecretDescriptor(
    SecretRef SecretRef,
    bool Exists,
    bool IsReadOnly,
    DateTimeOffset? UpdatedAtUtc = null,
    string? StorageDisplayName = null);
