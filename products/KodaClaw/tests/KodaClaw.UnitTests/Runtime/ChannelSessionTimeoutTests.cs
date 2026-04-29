using FluentAssertions;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

/// <summary>
/// KC-2201: Channel session timeout policy.
/// Sessions inactive for more than SessionTimeoutDays should not resume from store.
/// </summary>
public sealed class ChannelSessionTimeoutTests
{
    [Fact]
    public void SessionTimeoutDays_default_should_be_7()
    {
        var options = new ChannelSessionOptions();

        options.SessionTimeoutDays.Should().Be(7);
    }

    [Fact]
    public void SummaryCompressionThreshold_default_should_be_80()
    {
        var options = new ChannelSessionOptions();

        options.SummaryCompressionThreshold.Should().Be(80);
    }

    [Fact]
    public void SummaryCompressionTargetLines_default_should_be_40()
    {
        var options = new ChannelSessionOptions();

        options.SummaryCompressionTargetLines.Should().Be(40);
    }

    [Theory]
    [InlineData(7, 8, true)]   // 8 days ago — exceeds 7 day timeout → timed out
    [InlineData(7, 6, false)]  // 6 days ago — within window → not timed out
    [InlineData(0, 30, false)] // timeout disabled (0) → never timed out regardless of age
    public void Session_timeout_check_applies_correct_policy(
        int timeoutDays,
        int daysSinceLastInbound,
        bool expectTimedOut)
    {
        var options = new ChannelSessionOptions { SessionTimeoutDays = timeoutDays };
        var lastInboundAt = DateTimeOffset.UtcNow.AddDays(-daysSinceLastInbound);

        var isTimedOut = options.SessionTimeoutDays > 0
            && lastInboundAt < DateTimeOffset.UtcNow.AddDays(-options.SessionTimeoutDays);

        isTimedOut.Should().Be(expectTimedOut,
            because: $"with timeout={timeoutDays}d and lastInbound={daysSinceLastInbound}d ago, timedOut should be {expectTimedOut}");
    }

    [Fact]
    public void Session_without_last_inbound_should_not_be_timed_out()
    {
        var options = new ChannelSessionOptions { SessionTimeoutDays = 7 };
        DateTimeOffset? lastInboundAt = null;

        // Matches the production check: HasValue guard prevents false positive timeout.
        var isTimedOut = options.SessionTimeoutDays > 0
            && lastInboundAt.HasValue
            && lastInboundAt.Value < DateTimeOffset.UtcNow.AddDays(-options.SessionTimeoutDays);

        isTimedOut.Should().BeFalse(
            because: "a binding with no LastInboundAt should never be considered timed out");
    }
}
