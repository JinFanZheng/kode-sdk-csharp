namespace KodaClaw.Contracts.Models;

/// <summary>
/// A provider account aggregates credentials (API key, base URL) shared by
/// all models under this account. One account = one set of credentials + N models.
/// </summary>
public sealed record ProviderAccount(
    string Id,
    string DisplayName,
    ModelProviderKind ProviderKind,
    string? BaseUrl,
    string? ApiKeySecretRef,
    string? ApiKeyEnvironmentVariable,
    string? AccessMode,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string>? CustomHeaders = null);
