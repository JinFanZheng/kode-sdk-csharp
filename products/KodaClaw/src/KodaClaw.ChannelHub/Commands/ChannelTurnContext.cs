namespace KodaClaw.ChannelHub.Commands;

using KodaClaw.Contracts;

/// <summary>
/// Carries per-turn modifier state. Built from the combination of:
/// <list type="bullet">
///   <item>Sticky toggles persisted on <see cref="ThreadBinding"/> (/think on, /stream on).</item>
///   <item>Per-turn directives parsed from the inbound message (/think &lt;msg&gt;, /focus).</item>
/// </list>
/// Per-turn directives win over sticky state (the user's current-message intent beats prior settings).
/// </summary>
public sealed record ChannelTurnContext(
    string? PromptPrefix,
    bool? EnableThinking,
    int? ThinkingBudget,
    bool? EnableProgressStreamingOverride,
    string? FocusConstraint)
{
    /// <summary>An empty context — no directives applied.</summary>
    public static readonly ChannelTurnContext Empty = new(null, null, null, null, null);

    /// <summary>
    /// Builds a <see cref="ChannelTurnContext"/> from parsed directives only (no binding sticky state).
    /// Retained for unit tests and non-binding-aware call sites.
    /// </summary>
    public static ChannelTurnContext FromDirectives(ParsedChannelCommand parsed)
        => FromDirectivesAndBinding(parsed, binding: null);

    /// <summary>
    /// Builds a <see cref="ChannelTurnContext"/> by layering per-turn directives on top of
    /// the sticky toggles on <paramref name="binding"/>. Per-turn directives take precedence.
    /// </summary>
    public static ChannelTurnContext FromDirectivesAndBinding(ParsedChannelCommand parsed, ThreadBinding? binding)
    {
        // Sticky layer (from binding)
        bool? enableThinking = binding is { ThinkingEnabled: true } ? true : null;
        int? thinkingBudget = binding is { ThinkingEnabled: true } ? 8000 : null;
        bool? streamOverride = binding?.StreamOverride;
        string? focusConstraint = null;

        // Per-turn layer (directives override sticky)
        foreach (var directive in parsed.Directives)
        {
            switch (directive)
            {
                case ChannelDirectiveKind.Think:
                    enableThinking = true;
                    thinkingBudget = 8000;
                    break;
                case ChannelDirectiveKind.Focus:
                    focusConstraint = parsed.DirectiveArg;
                    break;
            }
        }

        return new ChannelTurnContext(
            PromptPrefix: null,
            EnableThinking: enableThinking,
            ThinkingBudget: thinkingBudget,
            EnableProgressStreamingOverride: streamOverride,
            FocusConstraint: focusConstraint);
    }
}
