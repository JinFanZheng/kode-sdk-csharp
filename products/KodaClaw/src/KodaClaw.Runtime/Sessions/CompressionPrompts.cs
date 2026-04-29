namespace KodaClaw.Runtime.Sessions;

/// <summary>
/// Compression prompts for channel sessions, split by thread type.
/// <list type="bullet">
///   <item><see cref="Dm"/> — direct message: one owner, personal context, workspace write-back allowed.</item>
///   <item><see cref="Group"/> — group chat: multiple participants, mention-triggered, no workspace write-back.</item>
/// </list>
/// </summary>
public static class ChannelCompressionPrompts
{
    /// <summary>
    /// Compression prompt for direct-message (DM) channel sessions.
    /// DM sessions have the same trust level as the main session — the owner is the sole
    /// participant, workspace write-back is enabled, and the conversation is personal.
    /// The summary should focus on: owner instructions, personal task progress, and
    /// any workspace or memory changes made during the session.
    /// </summary>
    public const string Dm =
        """
        You are compressing a direct-message channel conversation history to save context window space.
        Produce your response in EXACTLY this XML format and nothing else:

        <summary>
        Concise semantic summary of the removed messages (under 500 words).
        Include: owner instructions and requests, tasks completed or in progress,
                 replies sent, workspace or memory changes made,
                 channel delivery outcomes (success / failure / pending).
        Omit: repetitive polling, verbose system logs, transient error retries.
        </summary>

        <core-memory>
        ## Current Task
        [What the owner is asking Koda to accomplish right now]

        ## Recent Thread
        [Last 2-3 turns summarised with attribution]

        ## Workspace Changes
        [Any files written or memory entries appended during this session]

        ## Delivery Status
        [Pending or failed outbound messages, if any]

        ## Owner Preferences
        [Standing instructions or preferences expressed by the owner]
        </core-memory>
        """;

    /// <summary>
    /// Compression prompt for group channel sessions.
    /// Group sessions have multiple participants; Koda is mention-triggered and workspace
    /// write-back is disabled. The summary should focus on: group context, who said what,
    /// and what Koda replied — not file system state.
    /// </summary>
    public const string Group =
        """
        You are compressing a group-chat channel conversation history to save context window space.
        Produce your response in EXACTLY this XML format and nothing else:

        <summary>
        Concise semantic summary of the removed messages (under 500 words).
        Include: group name / platform, active participants and their roles (if known),
                 key questions or requests directed at Koda, replies sent by Koda,
                 channel delivery outcomes (success / failure / pending).
        Omit: messages not directed at Koda, repetitive chatter, verbose system logs.
        </summary>

        <core-memory>
        ## Group Context
        [Group name, platform, and purpose (if established)]

        ## Active Participants
        [Bullet list of people who have interacted with Koda, with any known roles]

        ## Recent Thread
        [Last 2-3 Koda-relevant turns with sender attribution]

        ## Delivery Status
        [Pending or failed outbound messages, if any]

        ## Standing Rules
        [Any group-specific instructions or rules given by participants]
        </core-memory>
        """;
}

/// <summary>
/// Compression prompt for automation sessions.
/// Automation sessions run headless scheduled tasks and need the summary to preserve
/// execution state and results rather than interactive dialogue.
/// </summary>
public static class AutomationCompressionPrompts
{
    public const string Default =
        """
        You are compressing an automation task history to save context window space.
        Produce your response in EXACTLY this XML format and nothing else:

        <summary>
        Concise semantic summary of the removed messages (under 500 words).
        Include: task name and trigger time, steps executed with success/failure status,
                 output artifacts produced (file paths, sent messages, API calls),
                 errors encountered and how they were handled.
        Omit: repetitive status polling, verbose command stdout, intermediate failed attempts.
        </summary>

        <core-memory>
        ## Task
        [One sentence: what automation is running and its trigger schedule]

        ## Execution Log
        [Bullet list of completed steps with outcome: ✓ success / ✗ failed / ⚠ partial]

        ## Outputs
        [Files written, messages sent, or external actions taken]

        ## Errors & Mitigations
        [Any errors encountered and how they were resolved or worked around]

        ## Next Steps
        [What remains to be done in this task run, if any]
        </core-memory>
        """;
}
