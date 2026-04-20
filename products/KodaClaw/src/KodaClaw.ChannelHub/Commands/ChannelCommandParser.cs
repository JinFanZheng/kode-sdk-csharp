namespace KodaClaw.ChannelHub.Commands;

/// <summary>
/// Parses inbound channel message text into a <see cref="ParsedChannelCommand"/>.
/// Static; no I/O; safe to call on any thread.
/// </summary>
public static class ChannelCommandParser
{
    private static readonly ParsedChannelCommand NoCommand =
        new(null, null, [], null, "");

    /// <summary>
    /// Parses the given text.
    /// <list type="bullet">
    ///   <item>Control commands (e.g. /new, /stop, /btw) intercept the turn entirely.</item>
    ///   <item>Directive modifiers (e.g. /think, /stream, /focus) modify how the turn runs;
    ///         the remaining text becomes <see cref="ParsedChannelCommand.CleanedText"/>.</item>
    ///   <item>Unknown or null text: returns a no-command result with CleanedText = original text.</item>
    /// </list>
    /// </summary>
    public static ParsedChannelCommand Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return NoCommand with { CleanedText = text ?? "" };

        var trimmed = text.Trim();

        if (!trimmed.StartsWith('/'))
            return new ParsedChannelCommand(null, null, [], null, text);

        // Extract the first token (the command word, including the leading slash)
        var spaceIdx = trimmed.IndexOf(' ');
        var commandToken = spaceIdx < 0 ? trimmed : trimmed[..spaceIdx];
        var remainder = spaceIdx < 0 ? "" : trimmed[(spaceIdx + 1)..].TrimStart();

        var def = ChannelCommandRegistry.Find(commandToken);
        if (def is null)
            return new ParsedChannelCommand(null, null, [], null, text);

        // ── Control command: intercepts the turn ─────────────────────────────────
        if (def.ControlKind.HasValue)
        {
            var controlArg = string.IsNullOrEmpty(remainder) ? null : remainder;

            // Special case: /think is a dual-mode command.
            //   /think on | /think off | /think (bare)   → ThinkToggle control command
            //   /think <anything else>                    → per-turn directive (legacy power-user path)
            if (def.ControlKind.Value == ChannelControlCommandKind.ThinkToggle
                && controlArg is not null
                && !IsToggleArg(controlArg))
            {
                // Per-turn directive fallback — re-parse via ParseDirective so the
                // remainder can still chain into /focus etc.
                return ParseDirective(ChannelDirectiveKind.Think, controlArg, text);
            }

            return new ParsedChannelCommand(
                ControlKind: def.ControlKind.Value,
                ControlArg: controlArg,
                Directives: [],
                DirectiveArg: null,
                CleanedText: text);
        }

        // ── Directive modifier: strip the token and continue ────────────────────
        if (def.DirectiveKind.HasValue)
        {
            return ParseDirective(def.DirectiveKind.Value, remainder, text);
        }

        // Should not happen (registry invariant), but fall back gracefully.
        return new ParsedChannelCommand(null, null, [], null, text);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the argument is the literal "on" or "off" (case-insensitive).
    /// Used by ThinkToggle to distinguish toggle usage from the per-turn directive fallback.
    /// </summary>
    internal static bool IsToggleArg(string arg)
    {
        // Only match when the argument is *exactly* on/off — not "on 其他" which should
        // still be treated as per-turn content (user typed a message starting with "on").
        var trimmed = arg.Trim();
        return string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedChannelCommand ParseDirective(
        ChannelDirectiveKind kind,
        string remainderAfterDirective,
        string originalText)
    {
        string? directiveArg = null;
        string cleanedText;

        if (kind == ChannelDirectiveKind.Focus)
        {
            // /focus <topic> <body>
            // The first token becomes the focus constraint; the rest is the actual prompt.
            var parts = remainderAfterDirective.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            directiveArg = parts.Length > 0 ? parts[0] : null;
            cleanedText = parts.Length > 1 ? parts[1].TrimStart() : "";
        }
        else
        {
            // /think <body>, /stream <body>, /quiet <body>
            cleanedText = remainderAfterDirective;
        }

        // Check if the remainder starts with another directive
        if (!string.IsNullOrEmpty(cleanedText) && cleanedText.StartsWith('/'))
        {
            var inner = Parse(cleanedText);
            if (inner.ControlKind is null && inner.Directives.Count > 0)
            {
                // Merge directives (chained: /think /stream <body>)
                var merged = new List<ChannelDirectiveKind> { kind };
                merged.AddRange(inner.Directives);
                return new ParsedChannelCommand(
                    ControlKind: null,
                    ControlArg: null,
                    Directives: merged,
                    DirectiveArg: directiveArg ?? inner.DirectiveArg,
                    CleanedText: inner.CleanedText);
            }
        }

        return new ParsedChannelCommand(
            ControlKind: null,
            ControlArg: null,
            Directives: [kind],
            DirectiveArg: directiveArg,
            CleanedText: cleanedText);
    }
}
