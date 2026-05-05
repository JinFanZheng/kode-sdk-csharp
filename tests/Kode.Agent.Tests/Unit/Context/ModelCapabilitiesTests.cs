using System.Net;
using System.Text;
using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Infrastructure.Providers;
using Xunit;

namespace Kode.Agent.Tests.Unit.Context;

/// <summary>
/// Tests for ModelCapabilities record and provider GetModelCapabilities().
/// </summary>
public sealed class ModelCapabilitiesTests
{
    // ── ModelCapabilities record ─────────────────────────────────────────────

    [Fact]
    public void ModelCapabilities_DefaultCompactionThresholdRatio_IsEightyPercent()
    {
        var caps = new ModelCapabilities { ContextWindow = 100_000 };
        caps.CompactionThresholdRatio.Should().Be(0.8);
    }

    [Fact]
    public void ModelCapabilities_PrefixCache_DefaultsToFalse()
    {
        var caps = new ModelCapabilities { ContextWindow = 100_000 };
        caps.SupportsPrefixCache.Should().BeFalse();
    }

    [Fact]
    public void ModelCapabilities_Defaults_NoCacheControl()
    {
        var caps = new ModelCapabilities { ContextWindow = 100_000 };
        caps.CacheControlType.Should().Be(CacheControlType.None);
    }

    [Fact]
    public void ModelCapabilities_Defaults_NoAlignedSummary()
    {
        var caps = new ModelCapabilities { ContextWindow = 100_000 };
        caps.SupportsCacheAlignedSummary.Should().BeFalse();
    }

    [Fact]
    public void ModelCapabilities_AllPropsSettable()
    {
        var caps = new ModelCapabilities
        {
            ContextWindow = 1_000_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.Ephemeral,
            CompactionThresholdRatio = 0.85,
            SupportsCacheAlignedSummary = true
        };

        caps.ContextWindow.Should().Be(1_000_000);
        caps.SupportsPrefixCache.Should().BeTrue();
        caps.CacheControlType.Should().Be(CacheControlType.Ephemeral);
        caps.CompactionThresholdRatio.Should().Be(0.85);
        caps.SupportsCacheAlignedSummary.Should().BeTrue();
    }

    // ── DeepSeekProvider GetModelCapabilities ────────────────────────────────

    [Theory]
    [InlineData("deepseek-v4-pro")]
    [InlineData("deepseek-v4-flash")]
    public void DeepSeekProvider_V4Models_HaveFullCapabilities(string modelId)
    {
        var provider = CreateDeepSeekProvider();
        var caps = provider.GetModelCapabilities(modelId);

        caps.Should().NotBeNull();
        caps!.ContextWindow.Should().Be(1_000_000);
        caps.SupportsPrefixCache.Should().BeTrue();
        caps.CacheControlType.Should().Be(CacheControlType.Ephemeral);
        caps.SupportsCacheAlignedSummary.Should().BeTrue();
    }

    [Theory]
    [InlineData("deepseek-chat")]
    [InlineData("deepseek-reasoner")]
    public void DeepSeekProvider_LegacyModels_HaveSmallerWindow(string modelId)
    {
        var provider = CreateDeepSeekProvider();
        var caps = provider.GetModelCapabilities(modelId);

        caps.Should().NotBeNull();
        caps!.ContextWindow.Should().Be(128_000);
        caps.SupportsPrefixCache.Should().BeTrue();
        caps.CacheControlType.Should().Be(CacheControlType.Ephemeral);
        caps.SupportsCacheAlignedSummary.Should().BeFalse(
            "cache-aligned summary requires >= 500K context window");
    }

    [Fact]
    public void DeepSeekProvider_UnknownModel_ReturnsNull()
    {
        var provider = CreateDeepSeekProvider();
        var caps = provider.GetModelCapabilities("unknown-model");

        caps.Should().BeNull("unknown models should return null for graceful fallback");
    }

    // ── AnthropicProvider GetModelCapabilities ───────────────────────────────

    [Theory]
    [InlineData("claude-sonnet-4-6-20250901")]
    [InlineData("claude-3-5-sonnet-20241022")]
    [InlineData("claude-3-5-haiku-20241022")]
    public void AnthropicProvider_ClaudeModels_HaveCapabilities(string modelId)
    {
        var provider = CreateAnthropicProvider();
        var caps = provider.GetModelCapabilities(modelId);

        caps.Should().NotBeNull();
        caps!.ContextWindow.Should().Be(200_000);
        caps.SupportsPrefixCache.Should().BeTrue();
        caps.CacheControlType.Should().Be(CacheControlType.Ephemeral);
        caps.SupportsCacheAlignedSummary.Should().BeFalse(
            "Anthropic prompt cache uses explicit breakpoints, not transparent prefix caching");
    }

    [Fact]
    public void AnthropicProvider_OpusModel_HasCapabilities()
    {
        var provider = CreateAnthropicProvider();
        var caps = provider.GetModelCapabilities("claude-opus-3-20250219");

        caps.Should().NotBeNull();
        caps!.ContextWindow.Should().Be(200_000);
    }

    [Fact]
    public void AnthropicProvider_UnknownModel_ReturnsNull()
    {
        var provider = CreateAnthropicProvider();
        var caps = provider.GetModelCapabilities("__nonexistent_model__");

        caps.Should().BeNull("completely unknown models should return null");
    }

    [Fact]
    public void AnthropicProvider_NonClaudeKnownModel_ReturnsCaps()
    {
        // The centralised registry is provider-agnostic: even though gpt-4o is not
        // a Claude model, querying AnthropicProvider for it returns capabilities
        // from the shared registry. This is intentional — providers delegate to
        // ModelCapabilitiesRegistry.Default.
        var provider = CreateAnthropicProvider();
        var caps = provider.GetModelCapabilities("gpt-4o");

        caps.Should().NotBeNull("the centralised registry knows about gpt-4o regardless of provider");
    }

    // ── OpenAIProvider GetModelCapabilities ──────────────────────────────────

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4o-mini")]
    public void OpenAIProvider_Gpt4oModels_HaveCapabilities(string modelId)
    {
        var provider = CreateOpenAIProvider();
        var caps = provider.GetModelCapabilities(modelId);

        caps.Should().NotBeNull();
        caps!.ContextWindow.Should().Be(128_000);
        caps.SupportsPrefixCache.Should().BeFalse();
        caps.CacheControlType.Should().Be(CacheControlType.None);
        caps.SupportsCacheAlignedSummary.Should().BeFalse();
    }

    [Theory]
    [InlineData("gpt-4.1-mini")]
    [InlineData("gpt-4.1")]
    public void OpenAIProvider_Gpt41Models_HaveLargeWindow(string modelId)
    {
        var provider = CreateOpenAIProvider();
        var caps = provider.GetModelCapabilities(modelId);

        caps.Should().NotBeNull();
        caps!.ContextWindow.Should().Be(1_000_000);
        caps.SupportsPrefixCache.Should().BeTrue();
        caps.CacheControlType.Should().Be(CacheControlType.PromptPrefix);
    }

    [Theory]
    [InlineData("o1")]
    [InlineData("o3-mini")]
    public void OpenAIProvider_ReasoningModels_HaveCapabilities(string modelId)
    {
        var provider = CreateOpenAIProvider();
        var caps = provider.GetModelCapabilities(modelId);

        caps.Should().NotBeNull();
        caps!.ContextWindow.Should().Be(200_000);
    }

    [Fact]
    public void OpenAIProvider_UnknownModel_ReturnsNull()
    {
        var provider = CreateOpenAIProvider();
        var caps = provider.GetModelCapabilities("unknown-model");

        caps.Should().BeNull();
    }

    // ── OpenAIResponsesProvider ──────────────────────────────────────────────

    [Fact]
    public void OpenAIResponsesProvider_DelegatesToSameCapabilities()
    {
        var provider = CreateOpenAIResponsesProvider();
        var caps = provider.GetModelCapabilities("gpt-4o");
        caps.Should().NotBeNull();
        caps!.ContextWindow.Should().Be(128_000);
    }

    // ── IModelProvider interface compliance ──────────────────────────────────

    [Fact]
    public void AllRealProviders_ImplementGetModelCapabilities()
    {
        var providers = new IModelProvider[]
        {
            CreateDeepSeekProvider(),
            CreateAnthropicProvider(),
            CreateOpenAIProvider(),
            CreateOpenAIResponsesProvider()
        };

        foreach (var provider in providers)
        {
            // Unknown model -> null (graceful fallback)
            var unknownCaps = provider.GetModelCapabilities("__nonexistent_model__");
            unknownCaps.Should().BeNull($"provider {provider.ProviderName} should return null for unknown model");

            // Known model -> non-null
            var known = provider.ProviderName switch
            {
                "deepseek" => provider.GetModelCapabilities("deepseek-v4-pro"),
                "anthropic" => provider.GetModelCapabilities("claude-sonnet-4-6-20250901"),
                "openai" => provider.GetModelCapabilities("gpt-4o"),
                "openai-responses" => provider.GetModelCapabilities("gpt-4o"),
                _ => null
            };
            known.Should().NotBeNull($"provider {provider.ProviderName} should return caps for known model");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DeepSeekProvider CreateDeepSeekProvider()
    {
        var handler = new StaticHttpMessageHandler("{}", "application/json");
        return new DeepSeekProvider(handler, new DeepSeekOptions { ApiKey = "test-key" });
    }

    private static AnthropicProvider CreateAnthropicProvider()
    {
        return new AnthropicProvider(new AnthropicOptions { ApiKey = "test-key" });
    }

    private static OpenAIProvider CreateOpenAIProvider()
    {
        return new OpenAIProvider(new OpenAIOptions { ApiKey = "test-key" });
    }

    private static OpenAIResponsesProvider CreateOpenAIResponsesProvider()
    {
        var handler = new StaticHttpMessageHandler("{}", "application/json");
        return new OpenAIResponsesProvider(handler, new OpenAIResponsesOptions { ApiKey = "test-key" });
    }

    private sealed class StaticHttpMessageHandler(string responseJson, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, mediaType)
            });
        }
    }
}
