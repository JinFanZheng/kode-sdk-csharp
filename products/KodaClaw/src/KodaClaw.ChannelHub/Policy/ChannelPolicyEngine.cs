using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;

namespace KodaClaw.ChannelHub.Policy;

public sealed class ChannelPolicyEngine
{
    public ChannelPolicy CreateDefaultPolicy(
        ChannelThreadType threadType,
        DateTimeOffset updatedAt,
        string? policyId = null)
    {
        if (updatedAt == default)
        {
            throw new ArgumentException("Updated timestamp is required.", nameof(updatedAt));
        }

        var id = string.IsNullOrWhiteSpace(policyId)
            ? BuildDefaultPolicyId(threadType)
            : policyId.Trim();

        return threadType switch
        {
            ChannelThreadType.DirectMessage => new ChannelPolicy(
                Id: id,
                ThreadType: ChannelThreadType.DirectMessage,
                UpdatedAt: updatedAt,
                LoadAgents: true,
                LoadIdentity: true,
                LoadSoul: true,
                LoadUserProfile: true,
                LoadLongTermMemory: false,
                LoadRecentThreadSummary: true,
                AllowDirectReply: true,
                RequireExplicitMention: false),
            ChannelThreadType.Group => new ChannelPolicy(
                Id: id,
                ThreadType: ChannelThreadType.Group,
                UpdatedAt: updatedAt,
                LoadAgents: true,
                LoadIdentity: true,
                LoadSoul: true,
                LoadUserProfile: false,
                LoadLongTermMemory: false,
                LoadRecentThreadSummary: true,
                AllowDirectReply: true,
                RequireExplicitMention: true),
            _ => throw new ArgumentOutOfRangeException(nameof(threadType), threadType, null),
        };
    }

    public ChannelPolicyDecision Evaluate(
        ChannelPolicy policy,
        bool hasExplicitMention = false)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var sessionKind = ResolveSessionKind(policy.ThreadType);
        var isMuted = policy.WorkspaceMuted || policy.ConnectorMuted || policy.ThreadMuted;
        var canDirectReply = !isMuted
            && policy.AllowDirectReply
            && (!policy.RequireExplicitMention || hasExplicitMention);

        // Iteration 5 v1 boundary: groups never read user/long-term memory.
        var effectiveLoadUserProfile = policy.ThreadType == ChannelThreadType.DirectMessage
            && policy.LoadUserProfile;

        // Iteration 5 v1 boundary: channel sessions do not load MEMORY.md.
        var effectiveLoadLongTermMemory = false;

        return new ChannelPolicyDecision(
            ThreadType: policy.ThreadType,
            SessionKind: sessionKind,
            IsMuted: isMuted,
            CanDirectReply: canDirectReply,
            LoadAgents: policy.LoadAgents,
            LoadIdentity: policy.LoadIdentity,
            LoadSoul: policy.LoadSoul,
            LoadUserProfile: effectiveLoadUserProfile,
            LoadLongTermMemory: effectiveLoadLongTermMemory,
            LoadRecentThreadSummary: policy.LoadRecentThreadSummary);
    }

    public static SessionKind ResolveSessionKind(ChannelThreadType threadType)
    {
        return threadType switch
        {
            ChannelThreadType.DirectMessage => SessionKind.ChannelDirectMessage,
            ChannelThreadType.Group => SessionKind.ChannelGroup,
            _ => throw new ArgumentOutOfRangeException(nameof(threadType), threadType, null),
        };
    }

    private static string BuildDefaultPolicyId(ChannelThreadType threadType)
    {
        return threadType switch
        {
            ChannelThreadType.DirectMessage => "policy-default-dm",
            ChannelThreadType.Group => "policy-default-group",
            _ => throw new ArgumentOutOfRangeException(nameof(threadType), threadType, null),
        };
    }
}
