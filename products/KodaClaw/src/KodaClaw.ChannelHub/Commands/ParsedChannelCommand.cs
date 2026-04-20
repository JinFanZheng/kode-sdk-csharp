namespace KodaClaw.ChannelHub.Commands;

/// <summary>
/// Identifies a channel-level control command (intercepts the turn — no agent run).
/// </summary>
public enum ChannelControlCommandKind
{
    NewSession,
    Status,
    Stop,
    Help,
    Compact,
    Tools,
    Info,
    SideQuestion,
    Model,
    ThinkToggle,
    StreamToggle,
}

/// <summary>
/// Identifies a per-turn directive modifier (does NOT intercept the turn, but modifies how it runs).
/// </summary>
public enum ChannelDirectiveKind
{
    Think,
    Focus,
}

/// <summary>
/// The result of parsing an inbound channel message for embedded commands/directives.
/// </summary>
/// <param name="ControlKind">Non-null when the message is a control command; causes the turn to be intercepted.</param>
/// <param name="ControlArg">The text remaining after the command keyword (trimmed); e.g. for /btw this is the question text.</param>
/// <param name="Directives">Zero or more directives that modify how the agent turn is executed.</param>
/// <param name="DirectiveArg">The first token after a directive that takes an argument (e.g. the topic for /focus).</param>
/// <param name="CleanedText">The user message with command tokens stripped; fed to the agent as the actual prompt.</param>
public sealed record ParsedChannelCommand(
    ChannelControlCommandKind? ControlKind,
    string? ControlArg,
    IReadOnlyList<ChannelDirectiveKind> Directives,
    string? DirectiveArg,
    string CleanedText);
