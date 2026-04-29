namespace KodaClaw.Contracts.Models;

/// <summary>
/// Structured pricing information for a model (per 1M tokens).
/// </summary>
public sealed record ModelPricing(
    decimal? InputTokenPrice = null,
    decimal? OutputTokenPrice = null,
    string Currency = "USD",
    string PricingModel = "pay-per-use",
    string? PricingNote = null);
