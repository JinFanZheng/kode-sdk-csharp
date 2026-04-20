using FluentAssertions;
using KodaClaw.ChannelHub.Commands;
using KodaClaw.Contracts;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

/// <summary>
/// Tests for ChannelTurnContext.FromDirectives (per-turn only) and
/// FromDirectivesAndBinding (sticky state + per-turn overlay).
/// </summary>
public sealed class ChannelTurnContextTests
{
    [Fact]
    public void Empty_directives_returns_empty_context()
    {
        var parsed = new ParsedChannelCommand(null, null, [], null, "hello");
        var ctx = ChannelTurnContext.FromDirectives(parsed);

        ctx.EnableThinking.Should().BeNull();
        ctx.ThinkingBudget.Should().BeNull();
        ctx.EnableProgressStreamingOverride.Should().BeNull();
        ctx.FocusConstraint.Should().BeNull();
        ctx.PromptPrefix.Should().BeNull();
    }

    [Fact]
    public void Think_directive_sets_thinking_budget()
    {
        var parsed = new ParsedChannelCommand(null, null, [ChannelDirectiveKind.Think], null, "分析代码");
        var ctx = ChannelTurnContext.FromDirectives(parsed);

        ctx.EnableThinking.Should().BeTrue();
        ctx.ThinkingBudget.Should().Be(8000);
        ctx.EnableProgressStreamingOverride.Should().BeNull();
    }

    [Fact]
    public void Focus_directive_sets_focus_constraint_from_DirectiveArg()
    {
        var parsed = new ParsedChannelCommand(null, null, [ChannelDirectiveKind.Focus], "代码质量", "审查这段代码");
        var ctx = ChannelTurnContext.FromDirectives(parsed);

        ctx.FocusConstraint.Should().Be("代码质量");
        ctx.EnableThinking.Should().BeNull();
        ctx.EnableProgressStreamingOverride.Should().BeNull();
    }

    [Fact]
    public void Empty_is_all_null()
    {
        var ctx = ChannelTurnContext.Empty;
        ctx.EnableThinking.Should().BeNull();
        ctx.ThinkingBudget.Should().BeNull();
        ctx.EnableProgressStreamingOverride.Should().BeNull();
        ctx.FocusConstraint.Should().BeNull();
        ctx.PromptPrefix.Should().BeNull();
    }

    // ── Sticky toggle overlay ────────────────────────────────────────────────

    [Fact]
    public void Sticky_thinking_enabled_on_binding_lifts_to_context()
    {
        var parsed = new ParsedChannelCommand(null, null, [], null, "hello");
        var binding = CreateBinding(thinkingEnabled: true, streamOverride: null);

        var ctx = ChannelTurnContext.FromDirectivesAndBinding(parsed, binding);

        ctx.EnableThinking.Should().BeTrue();
        ctx.ThinkingBudget.Should().Be(8000);
        ctx.EnableProgressStreamingOverride.Should().BeNull();
    }

    [Fact]
    public void Sticky_stream_override_on_binding_lifts_to_context()
    {
        var parsed = new ParsedChannelCommand(null, null, [], null, "hello");
        var bindingOn = CreateBinding(thinkingEnabled: false, streamOverride: true);
        var bindingOff = CreateBinding(thinkingEnabled: false, streamOverride: false);

        ChannelTurnContext.FromDirectivesAndBinding(parsed, bindingOn).EnableProgressStreamingOverride
            .Should().BeTrue();
        ChannelTurnContext.FromDirectivesAndBinding(parsed, bindingOff).EnableProgressStreamingOverride
            .Should().BeFalse();
    }

    [Fact]
    public void PerTurn_think_directive_wins_when_sticky_off()
    {
        // Sticky thinking off, but this turn user explicitly invoked /think <message>
        var parsed = new ParsedChannelCommand(null, null, [ChannelDirectiveKind.Think], null, "复杂分析");
        var binding = CreateBinding(thinkingEnabled: false, streamOverride: null);

        var ctx = ChannelTurnContext.FromDirectivesAndBinding(parsed, binding);

        ctx.EnableThinking.Should().BeTrue();
        ctx.ThinkingBudget.Should().Be(8000);
    }

    [Fact]
    public void PerTurn_think_is_idempotent_when_sticky_on()
    {
        var parsed = new ParsedChannelCommand(null, null, [ChannelDirectiveKind.Think], null, "复杂分析");
        var binding = CreateBinding(thinkingEnabled: true, streamOverride: null);

        var ctx = ChannelTurnContext.FromDirectivesAndBinding(parsed, binding);

        ctx.EnableThinking.Should().BeTrue();
        ctx.ThinkingBudget.Should().Be(8000);
    }

    [Fact]
    public void Null_binding_falls_back_to_per_turn_only()
    {
        var parsed = new ParsedChannelCommand(null, null, [ChannelDirectiveKind.Think], null, "x");
        var ctx = ChannelTurnContext.FromDirectivesAndBinding(parsed, binding: null);

        ctx.EnableThinking.Should().BeTrue();
        ctx.EnableProgressStreamingOverride.Should().BeNull();
    }

    // Construct a minimal ThreadBinding for tests — only fields the factory reads matter.
    private static ThreadBinding CreateBinding(bool thinkingEnabled, bool? streamOverride)
        => new(
            Id: "b-1",
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: "a-1",
            ExternalThreadId: "t-1",
            ThreadType: ChannelThreadType.DirectMessage,
            SessionId: "s-1",
            SessionKind: SessionKind.ChannelDirectMessage,
            ChannelIdentity: new ChannelIdentity("u-1", null, null),
            PolicyId: "default",
            DeliveryRuleId: "default",
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ThinkingEnabled: thinkingEnabled,
            StreamOverride: streamOverride);
}
