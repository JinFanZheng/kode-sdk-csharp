namespace KodaClaw.Contracts.Channels;

public sealed record ChannelPolicy(
    string Id,
    ChannelThreadType ThreadType,
    DateTimeOffset UpdatedAt,
    bool LoadAgents = true,
    bool LoadIdentity = true,
    bool LoadSoul = true,
    bool LoadUserProfile = false,
    bool LoadLongTermMemory = false,
    bool LoadRecentThreadSummary = true,
    bool AllowDirectReply = true,
    bool RequireExplicitMention = false,
    bool WorkspaceMuted = false,
    bool ConnectorMuted = false,
    bool ThreadMuted = false,
    string? Notes = null);
