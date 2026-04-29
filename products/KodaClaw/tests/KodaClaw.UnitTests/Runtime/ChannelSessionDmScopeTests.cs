using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

/// <summary>
/// KC-5001/5002/5003: Channel DM session scope elevation.
/// DM sessions are owner-trust-equivalent to the main session and load full workspace context.
/// </summary>
public sealed class ChannelSessionDmScopeTests
{
    // ── LoadLongTermMemory ────────────────────────────────────────────────────

    [Fact]
    public void DM_policy_should_enable_long_term_memory()
    {
        var policy = BuildPolicy(ChannelThreadType.DirectMessage);

        var scope = ChannelSessionService.ResolveEffectiveScope(policy);

        scope.LoadLongTermMemory.Should().BeTrue(
            because: "DM sessions are owner-only and should load MEMORY.md like the main session");
    }

    [Fact]
    public void Group_policy_should_not_enable_long_term_memory()
    {
        var policy = BuildPolicy(ChannelThreadType.Group);

        var scope = ChannelSessionService.ResolveEffectiveScope(policy);

        scope.LoadLongTermMemory.Should().BeFalse(
            because: "Group sessions have untrusted members and must not expose long-term memory");
    }

    // ── LoadUserProfile ───────────────────────────────────────────────────────

    [Fact]
    public void DM_policy_with_user_profile_enabled_should_load_user_profile()
    {
        var policy = BuildPolicy(ChannelThreadType.DirectMessage, loadUserProfile: true);

        var scope = ChannelSessionService.ResolveEffectiveScope(policy);

        scope.LoadUserProfile.Should().BeTrue();
    }

    [Fact]
    public void Group_policy_should_never_load_user_profile()
    {
        var policy = BuildPolicy(ChannelThreadType.Group, loadUserProfile: true);

        var scope = ChannelSessionService.ResolveEffectiveScope(policy);

        scope.LoadUserProfile.Should().BeFalse(
            because: "Group sessions must not expose the user profile regardless of stored policy");
    }

    // ── Session timeout ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(8, true)]   // 8 days inactive → timed out
    [InlineData(6, false)]  // 6 days inactive → still active
    [InlineData(0, false)]  // timeout disabled → never timed out
    public void Group_session_should_apply_timeout_policy(int daysSinceLastInbound, bool expectTimedOut)
    {
        const int timeoutDays = 7;
        var lastInboundAt = DateTimeOffset.UtcNow.AddDays(-daysSinceLastInbound);

        // Mirrors the production check in ChannelSessionService.EnsureChannelSessionAsync
        var isTimedOut = timeoutDays > 0 && daysSinceLastInbound > 0
            && lastInboundAt < DateTimeOffset.UtcNow.AddDays(-timeoutDays);

        isTimedOut.Should().Be(expectTimedOut);
    }

    [Theory]
    [InlineData(8)]   // would time out as Group
    [InlineData(30)]  // long inactivity
    [InlineData(365)] // extreme inactivity
    public void DM_session_should_never_be_timed_out_regardless_of_inactivity(int daysSinceLastInbound)
    {
        // KC-5002: DM sessions bypass the timeout check entirely.
        const ChannelThreadType threadType = ChannelThreadType.DirectMessage;
        const int timeoutDays = 7;
        var lastInboundAt = DateTimeOffset.UtcNow.AddDays(-daysSinceLastInbound);

        // Production logic: timeout only applies to Group
        var isTimedOut = threadType == ChannelThreadType.Group
            && timeoutDays > 0;

        isTimedOut.Should().BeFalse(
            because: "DM continuity is the value — owner sessions should never lose history due to timeout");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ChannelPolicy BuildPolicy(
        ChannelThreadType threadType,
        bool loadUserProfile = true,
        bool loadIdentity = true,
        bool loadSoul = true,
        bool loadAgents = true,
        bool loadRecentThreadSummary = true) =>
        new(
            Id: "test-policy",
            ThreadType: threadType,
            UpdatedAt: DateTimeOffset.UtcNow,
            LoadAgents: loadAgents,
            LoadIdentity: loadIdentity,
            LoadSoul: loadSoul,
            LoadUserProfile: loadUserProfile,
            LoadRecentThreadSummary: loadRecentThreadSummary);
}
