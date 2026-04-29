using FluentAssertions;
using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Delivery;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

public sealed class ChannelApprovalResponseParserTests
{
    // ── Approve keywords ────────────────────────────────────────────────────

    [Theory]
    [InlineData("ok")]
    [InlineData("OK")]
    [InlineData("Ok")]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("approve")]
    [InlineData("APPROVE")]
    [InlineData("send")]
    [InlineData("SEND")]
    public void Should_detect_approve_without_token(string input)
    {
        var result = ChannelApprovalResponseParser.TryParse(input);

        result.Should().NotBeNull();
        result!.Action.Should().Be(ApprovalAction.Approve);
        result.Token.Should().BeNull();
    }

    // ── Reject keywords ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("no")]
    [InlineData("NO")]
    [InlineData("No")]
    [InlineData("cancel")]
    [InlineData("CANCEL")]
    [InlineData("reject")]
    [InlineData("REJECT")]
    public void Should_detect_reject_without_token(string input)
    {
        var result = ChannelApprovalResponseParser.TryParse(input);

        result.Should().NotBeNull();
        result!.Action.Should().Be(ApprovalAction.Reject);
        result.Token.Should().BeNull();
    }

    // ── With token ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ok A3F9C1", ApprovalAction.Approve, "A3F9C1")]
    [InlineData("ok a3f9c1", ApprovalAction.Approve, "A3F9C1")]   // lowercase token normalised
    [InlineData("yes B1C2D3", ApprovalAction.Approve, "B1C2D3")]
    [InlineData("no A3F9C1", ApprovalAction.Reject, "A3F9C1")]
    [InlineData("cancel FFFFFF", ApprovalAction.Reject, "FFFFFF")]
    [InlineData("reject 000000", ApprovalAction.Reject, "000000")]
    public void Should_detect_intent_with_valid_token(string input, ApprovalAction action, string expectedToken)
    {
        var result = ChannelApprovalResponseParser.TryParse(input);

        result.Should().NotBeNull();
        result!.Action.Should().Be(action);
        result.Token.Should().Be(expectedToken);
    }

    // ── Not recognised ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ok 去发吧")]           // second word not a hex token
    [InlineData("ok GGGGGG")]          // G is not valid hex
    [InlineData("ok ABC12")]           // 5 chars, not 6
    [InlineData("ok ABC1234")]         // 7 chars, not 6
    [InlineData("ok A3F9C1 extra")]    // three words
    [InlineData("sure")]               // unknown keyword
    [InlineData("yep")]                // unknown keyword
    [InlineData("发送")]               // non-ASCII
    public void Should_return_null_for_non_approval_text(string? input)
    {
        var result = ChannelApprovalResponseParser.TryParse(input);

        result.Should().BeNull();
    }

    // ── Token determinism ───────────────────────────────────────────────────

    [Fact]
    public void BuildApprovalToken_should_produce_6_uppercase_hex_chars()
    {
        var token = ChannelDeliveryGovernanceService.BuildApprovalToken("draft-abc123");

        token.Should().HaveLength(6);
        token.Should().MatchRegex("^[0-9A-F]{6}$");
    }

    [Fact]
    public void BuildApprovalToken_should_be_deterministic()
    {
        var token1 = ChannelDeliveryGovernanceService.BuildApprovalToken("draft-xyz");
        var token2 = ChannelDeliveryGovernanceService.BuildApprovalToken("draft-xyz");

        token1.Should().Be(token2);
    }

    [Fact]
    public void BuildApprovalToken_should_differ_for_different_draft_ids()
    {
        var token1 = ChannelDeliveryGovernanceService.BuildApprovalToken("draft-aaa");
        var token2 = ChannelDeliveryGovernanceService.BuildApprovalToken("draft-bbb");

        token1.Should().NotBe(token2);
    }
}
