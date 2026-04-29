using FluentAssertions;
using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Turn;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

/// <summary>
/// Tests for <see cref="ChannelTurnOrchestrator.FormatChannelError"/>.
/// Verifies that all known error patterns map to the correct user-facing messages.
/// </summary>
public sealed class FormatChannelErrorTests
{
    // ── Null / empty ──────────────────────────────────────────────────────────

    [Fact]
    public void NullMessage_ReturnsGenericRetryPrompt()
    {
        ChannelTurnOrchestrator.FormatChannelError(null)
            .Should().Contain("请稍后重试");
    }

    // ── Content safety ────────────────────────────────────────────────────────

    [Fact]
    public void ModelEmptyResponse_ReturnsContentSafetyMessage()
    {
        ChannelTurnOrchestrator.FormatChannelError("model_empty_response")
            .Should().Contain("内容安全过滤");
    }

    // ── Rate limit / overloaded ───────────────────────────────────────────────

    [Theory]
    [InlineData("访问量过大，请稍后再试")]
    [InlineData("您的账户已达到速率限制，请您控制请求频率")]
    [InlineData("upstream: 429 Too Many Requests")]
    [InlineData("529 Overloaded")]
    [InlineData("model overloaded")]
    [InlineData("Rate Limit exceeded")]
    [InlineData("rate limit error: you have reached the limit")]
    public void RateLimitPatterns_ReturnsOverloadedMessage(string errorMessage)
    {
        ChannelTurnOrchestrator.FormatChannelError(errorMessage)
            .Should().Contain("访问量过大")
            .And.Contain("请稍后重试");
    }

    // ── Timeout ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("request timed out after 30s")]
    [InlineData("The operation has timed out")]
    [InlineData("Connection Timeout")]
    public void TimeoutPatterns_ReturnsTimeoutMessage(string errorMessage)
    {
        ChannelTurnOrchestrator.FormatChannelError(errorMessage)
            .Should().Contain("超时")
            .And.Contain("请稍后重试");
    }

    // ── Generic error ─────────────────────────────────────────────────────────

    [Fact]
    public void UnknownError_ReturnsMessageWithOriginalText()
    {
        var result = ChannelTurnOrchestrator.FormatChannelError("some unexpected error");
        result.Should().Contain("some unexpected error");
    }
}
