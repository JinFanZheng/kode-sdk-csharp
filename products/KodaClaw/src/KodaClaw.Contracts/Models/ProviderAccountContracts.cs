namespace KodaClaw.Contracts.Models;

// ── Request DTOs ─────────────────────────────────────────────

public sealed record CreateProviderAccountRequest(
    string DisplayName,
    ModelProviderKind ProviderKind,
    string? BaseUrl,
    string? ApiKeyValue,
    string? ApiKeyEnvironmentVariable,
    string? AccessMode,
    IReadOnlyDictionary<string, string>? CustomHeaders,
    IReadOnlyList<CreateAccountModelRequest> Models);

public sealed record CreateAccountModelRequest(
    string DisplayName,
    string ModelId,
    ModelCapabilitySet Capabilities = ModelCapabilitySet.Text,
    int ContextWindowSize = 128_000,
    int MaxOutputTokens = 8192,
    bool IsReasoning = false,
    bool SupportsToolCalling = true,
    bool IsDefaultForAccount = false,
    bool IsGlobalDefault = false,
    ModelPricing? Pricing = null);

public sealed record UpdateProviderAccountRequest(
    string? DisplayName = null,
    string? BaseUrl = null,
    string? ApiKeyValue = null,
    string? ApiKeyEnvironmentVariable = null,
    bool? Enabled = null,
    IReadOnlyDictionary<string, string>? CustomHeaders = null);

public sealed record UpdateAccountModelRequest(
    string? DisplayName = null,
    string? ModelId = null,
    ModelCapabilitySet? Capabilities = null,
    int? ContextWindowSize = null,
    int? MaxOutputTokens = null,
    bool? IsReasoning = null,
    bool? SupportsToolCalling = null,
    bool? Enabled = null,
    ModelPricing? Pricing = null);

// ── Response DTOs ────────────────────────────────────────────

public sealed record ProviderAccountResponse(
    string Id,
    string DisplayName,
    ModelProviderKind ProviderKind,
    string? BaseUrl,
    string? AccessMode,
    bool Enabled,
    bool HasApiKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AccountModelResponse> Models,
    // Non-sensitive account fields exposed so the "edit endpoint" UI can pre-fill
    // without a second fetch. ApiKeySecretRef stays hidden — clients only see HasApiKey.
    string? ApiKeyEnvironmentVariable = null,
    IReadOnlyDictionary<string, string>? CustomHeaders = null);

public sealed record AccountModelResponse(
    string Id,
    string AccountId,
    string DisplayName,
    string ModelId,
    ModelCapabilitySet Capabilities,
    bool IsDefaultForAccount,
    bool IsGlobalDefault,
    bool Enabled,
    int ContextWindowSize,
    int MaxOutputTokens,
    bool IsReasoning,
    bool SupportsToolCalling,
    ModelPricing? Pricing);
