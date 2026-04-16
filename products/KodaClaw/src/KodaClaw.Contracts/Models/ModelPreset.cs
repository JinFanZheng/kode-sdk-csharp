namespace KodaClaw.Contracts;

public sealed record ModelPreset(
    string PresetId,
    string DisplayName,
    string Provider,
    string ModelId,
    string? BaseUrl,
    int ContextWindowSize,
    string Tier,            // "Recommended" | "Advanced" | "Fast" | "Reasoning" | "Local"
    string Description,
    bool RequiresBaseUrl,
    ModelCapabilitySet DefaultCapabilities = ModelCapabilitySet.Text,
    int MaxOutputTokens = 8192,
    bool IsReasoning = false,
    bool SupportsToolCalling = true,
    string? AccessMode = null,
    string? AnthropicBaseUrl = null,
    string? Group = null,
    ModelPricing? Pricing = null);
