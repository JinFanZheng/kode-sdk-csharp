using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;

namespace KodaClaw.ChannelHub.Policy;

public sealed record ChannelPolicyDecision(
    ChannelThreadType ThreadType,
    SessionKind SessionKind,
    bool IsMuted,
    bool CanDirectReply,
    bool LoadAgents,
    bool LoadIdentity,
    bool LoadSoul,
    bool LoadUserProfile,
    bool LoadLongTermMemory,
    bool LoadRecentThreadSummary);
