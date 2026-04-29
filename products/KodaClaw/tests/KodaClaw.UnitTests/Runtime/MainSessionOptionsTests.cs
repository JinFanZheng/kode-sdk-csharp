using FluentAssertions;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class MainSessionOptionsTests
{
    [Fact]
    public void Default_options_have_compression_ratio_within_valid_range()
    {
        var options = new MainSessionOptions();
        options.ContextCompressionTriggerRatio.Should().BeGreaterThan(0).And.BeLessThan(1,
            because: "trigger ratio must be a fraction of the context window");
        options.ContextCompressionTargetRatio.Should().BeGreaterThan(0).And.BeLessThan(1,
            because: "target ratio must be a fraction of the context window");
    }

    [Fact]
    public void Default_compression_target_is_lower_than_trigger()
    {
        var options = new MainSessionOptions();
        options.ContextCompressionTargetRatio.Should().BeLessThan(options.ContextCompressionTriggerRatio,
            because: "compression target must be lower than the trigger threshold");
    }

    [Fact]
    public void Default_context_window_size_is_reasonable()
    {
        var options = new MainSessionOptions();
        options.DefaultContextWindowSize.Should().BeGreaterThanOrEqualTo(16_000,
            because: "even small-context models need at least 16k");
    }

    [Fact]
    public void Computed_thresholds_are_valid_for_default_window()
    {
        var options = new MainSessionOptions();
        var maxTokens = (int)(options.DefaultContextWindowSize * options.ContextCompressionTriggerRatio);
        var compressTo = (int)(options.DefaultContextWindowSize * options.ContextCompressionTargetRatio);
        maxTokens.Should().BeGreaterThan(0);
        compressTo.Should().BeGreaterThan(0);
        compressTo.Should().BeLessThan(maxTokens,
            because: "compression target must be lower than trigger threshold");
    }
}
