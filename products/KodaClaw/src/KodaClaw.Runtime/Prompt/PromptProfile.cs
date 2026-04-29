using KodaClaw.Contracts.Channels;

namespace KodaClaw.Runtime.Prompt;

public sealed record PromptProfile(
    PromptProfileId Id,
    string Title,
    string BaseInstruction,
    string? OverlayInstruction = null);

public static class PromptProfiles
{
    public static PromptProfile Bootstrap(string? systemPrompt = null)
    {
        return new PromptProfile(
            PromptProfileId.Bootstrap,
            "Bootstrap",
            ResolveInstruction(systemPrompt, "You are KodaClaw bootstrap assistant."),
            "Learn who the user is, what Koda should optimize for, and which durable rules belong in identity, soul, and user files.");
    }

    public static PromptProfile Main(string? systemPrompt = null)
    {
        return new PromptProfile(
            PromptProfileId.Main,
            "Main",
            ResolveInstruction(systemPrompt, "You are KodaClaw main assistant."),
            "Operate as the primary local-first assistant and keep risky actions observable and approval-aware.");
    }

    public static PromptProfile Automation(string? systemPrompt = null)
    {
        return new PromptProfile(
            PromptProfileId.Automation,
            "Automation",
            ResolveInstruction(systemPrompt, "You are KodaClaw automation assistant."),
            "Treat the automation definition as the primary objective. Use only the explicitly loaded workspace files, keep long-term memory out unless MEMORY.md was explicitly loaded, and keep outputs concise.");
    }

    public static PromptProfile Channel(ChannelThreadType threadType, string? systemPrompt = null)
    {
        return threadType switch
        {
            ChannelThreadType.DirectMessage => new PromptProfile(
                PromptProfileId.ChannelDirectMessage,
                "Channel Direct Message",
                ResolveInstruction(systemPrompt, "You are KodaClaw channel assistant."),
                "Operate as a bounded delegate inside a private conversation. Use only approved direct-message context, reflect loaded user preferences when relevant, and never imply that a draft was already sent."),
            ChannelThreadType.Group => new PromptProfile(
                PromptProfileId.ChannelGroup,
                "Channel Group",
                ResolveInstruction(systemPrompt, "You are KodaClaw channel assistant."),
                "Assume public visibility and incomplete context. Avoid personal memory recall, keep replies public-safe, and prefer observing over replying unless Koda is explicitly mentioned or clearly asked to respond."),
            _ => throw new ArgumentOutOfRangeException(nameof(threadType), threadType, null),
        };
    }

    private static string ResolveInstruction(string? configured, string fallback)
    {
        return string.IsNullOrWhiteSpace(configured)
            ? fallback
            : configured.Trim();
    }
}
