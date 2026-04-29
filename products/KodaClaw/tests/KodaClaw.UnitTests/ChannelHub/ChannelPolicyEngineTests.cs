using FluentAssertions;
using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Policy;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

public sealed class ChannelPolicyEngineTests
{
    [Fact]
    public void CreateDefaultPolicy_should_build_direct_message_defaults()
    {
        var engine = new ChannelPolicyEngine();
        var updatedAt = new DateTimeOffset(2026, 3, 19, 1, 0, 0, TimeSpan.Zero);

        var policy = engine.CreateDefaultPolicy(ChannelThreadType.DirectMessage, updatedAt);

        policy.Id.Should().Be("policy-default-dm");
        policy.ThreadType.Should().Be(ChannelThreadType.DirectMessage);
        policy.LoadUserProfile.Should().BeTrue();
        policy.LoadLongTermMemory.Should().BeFalse();
        policy.RequireExplicitMention.Should().BeFalse();
        policy.AllowDirectReply.Should().BeTrue();
    }

    [Fact]
    public void CreateDefaultPolicy_should_build_group_defaults()
    {
        var engine = new ChannelPolicyEngine();
        var updatedAt = new DateTimeOffset(2026, 3, 19, 1, 5, 0, TimeSpan.Zero);

        var policy = engine.CreateDefaultPolicy(ChannelThreadType.Group, updatedAt);

        policy.Id.Should().Be("policy-default-group");
        policy.ThreadType.Should().Be(ChannelThreadType.Group);
        policy.LoadUserProfile.Should().BeFalse();
        policy.LoadLongTermMemory.Should().BeFalse();
        policy.RequireExplicitMention.Should().BeTrue();
        policy.AllowDirectReply.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_should_enforce_group_memory_boundary_even_if_policy_requests_more()
    {
        var engine = new ChannelPolicyEngine();
        var policy = new ChannelPolicy(
            Id: "policy-group-custom",
            ThreadType: ChannelThreadType.Group,
            UpdatedAt: new DateTimeOffset(2026, 3, 19, 2, 0, 0, TimeSpan.Zero),
            LoadUserProfile: true,
            LoadLongTermMemory: true,
            AllowDirectReply: true,
            RequireExplicitMention: false);

        var decision = engine.Evaluate(policy);

        decision.SessionKind.Should().Be(SessionKind.ChannelGroup);
        decision.LoadUserProfile.Should().BeFalse();
        decision.LoadLongTermMemory.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_should_block_direct_reply_when_any_mute_flag_is_on()
    {
        var engine = new ChannelPolicyEngine();
        var policy = new ChannelPolicy(
            Id: "policy-dm-muted",
            ThreadType: ChannelThreadType.DirectMessage,
            UpdatedAt: new DateTimeOffset(2026, 3, 19, 2, 10, 0, TimeSpan.Zero),
            LoadUserProfile: true,
            WorkspaceMuted: false,
            ConnectorMuted: true,
            ThreadMuted: false,
            AllowDirectReply: true,
            RequireExplicitMention: false);

        var decision = engine.Evaluate(policy);

        decision.IsMuted.Should().BeTrue();
        decision.CanDirectReply.Should().BeFalse();
        decision.SessionKind.Should().Be(SessionKind.ChannelDirectMessage);
    }

    [Fact]
    public void Evaluate_should_require_explicit_mention_when_policy_requires_it()
    {
        var engine = new ChannelPolicyEngine();
        var policy = new ChannelPolicy(
            Id: "policy-group-mention",
            ThreadType: ChannelThreadType.Group,
            UpdatedAt: new DateTimeOffset(2026, 3, 19, 2, 15, 0, TimeSpan.Zero),
            AllowDirectReply: true,
            RequireExplicitMention: true);

        var withoutMention = engine.Evaluate(policy, hasExplicitMention: false);
        var withMention = engine.Evaluate(policy, hasExplicitMention: true);

        withoutMention.CanDirectReply.Should().BeFalse();
        withMention.CanDirectReply.Should().BeTrue();
    }
}
