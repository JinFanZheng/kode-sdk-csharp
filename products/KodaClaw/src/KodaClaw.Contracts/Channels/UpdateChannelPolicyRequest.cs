namespace KodaClaw.Contracts.Channels;

public sealed record UpdateChannelPolicyRequest(
    bool LoadAgents,
    bool LoadIdentity,
    bool LoadSoul,
    bool LoadUserProfile,
    bool LoadLongTermMemory,
    bool LoadRecentThreadSummary,
    bool AllowDirectReply,
    bool RequireExplicitMention,
    bool WorkspaceMuted,
    bool ConnectorMuted,
    bool ThreadMuted,
    string? Notes = null);
