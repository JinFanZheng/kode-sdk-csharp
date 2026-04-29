namespace KodaClaw.Contracts.Models;

/// <summary>
/// An individual model bound to a <see cref="ProviderAccount"/>.
/// </summary>
public sealed record AccountModel(
    string Id,
    string AccountId,
    string DisplayName,
    string ModelId,
    ModelCapabilitySet Capabilities,
    bool IsDefaultForAccount,
    bool IsGlobalDefault,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int ContextWindowSize = 128_000,
    int MaxOutputTokens = 8192,
    bool IsReasoning = false,
    bool SupportsToolCalling = true,
    ModelPricing? Pricing = null);
