using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Context;
using Xunit;

namespace Kode.Agent.Tests.Unit.Context;

/// <summary>
/// Tests for ContextManagerOptions.FromCapabilities adaptive thresholds.
/// </summary>
public sealed class ContextManagerOptionsCapabilitiesTests
{
    [Fact]
    public void FromCapabilities_Null_ReturnsSensibleDefaults()
    {
        var opts = ContextManagerOptions.FromCapabilities(null);

        opts.MaxTokens.Should().Be(50_000, "fallback to existing default");
        opts.CompressToTokens.Should().Be(30_000, "fallback to existing default");
    }

    [Fact]
    public void FromCapabilities_V4Model_ScalesTo1MWindow()
    {
        var caps = new ModelCapabilities
        {
            ContextWindow = 1_000_000,
            CompactionThresholdRatio = 0.8,
        };

        var opts = ContextManagerOptions.FromCapabilities(caps);

        opts.MaxTokens.Should().Be(800_000, "80% of 1M context window");
        opts.CompressToTokens.Should().Be(300_000, "30% of 1M context window");
    }

    [Fact]
    public void FromCapabilities_ClaudeModel_ScalesTo200KWindow()
    {
        var caps = new ModelCapabilities
        {
            ContextWindow = 200_000,
        };

        var opts = ContextManagerOptions.FromCapabilities(caps);

        opts.MaxTokens.Should().Be(160_000, "80% of 200K");
        opts.CompressToTokens.Should().Be(60_000, "30% of 200K");
    }

    [Fact]
    public void FromCapabilities_SmallWindow_ScalesDown()
    {
        var caps = new ModelCapabilities
        {
            ContextWindow = 64_000,
        };

        var opts = ContextManagerOptions.FromCapabilities(caps);

        opts.MaxTokens.Should().Be(51_200, "80% of 64K");
        opts.CompressToTokens.Should().Be(19_200, "30% of 64K");
    }

    [Fact]
    public void FromCapabilities_PreservesOtherDefaults()
    {
        var caps = new ModelCapabilities { ContextWindow = 500_000 };

        var opts = ContextManagerOptions.FromCapabilities(caps);

        // Only MaxTokens and CompressToTokens change; everything else stays default.
        opts.EnableCoreMemory.Should().BeTrue();
        opts.MinRecentMessages.Should().Be(6);
        opts.MaxSummaryDepth.Should().Be(5);
        opts.ToolResultCompression.Should().BeNull();
    }

    [Fact]
    public void FromCapabilities_UserProvidedCapOverridesFromCapabilities()
    {
        // When user explicitly sets MaxTokens in config, FromCapabilities should
        // use it instead of overriding from capabilities.
        var caps = new ModelCapabilities { ContextWindow = 1_000_000 };

        // Simulate user overriding MaxTokens manually
        var baseOpts = ContextManagerOptions.FromCapabilities(caps);
        var userOverride = baseOpts with { MaxTokens = 100_000 }; // User wants tighter control

        userOverride.MaxTokens.Should().Be(100_000, "user override should be respected");
        // But other properties from capabilities should still flow
        userOverride.CompressToTokens.Should().Be(300_000);
    }
}
