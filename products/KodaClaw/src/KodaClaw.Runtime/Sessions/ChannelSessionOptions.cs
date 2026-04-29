using Kode.Agent.Sdk.Core.Types;

namespace KodaClaw.Runtime.Sessions;

public sealed class ChannelSessionOptions
{
    public string Model { get; init; } = "koda-main";

    public string? SystemPrompt { get; init; } = "You are KodaClaw channel assistant.";

    public int MaxIterations { get; init; } = 15;

    public int MaxPromptCharacters { get; init; } = 16000;

    public IReadOnlyList<string> Tools { get; init; } = MainSessionOptions.DefaultTools;

    // Channel sessions never pause for mid-turn tool approval.
    // The only approval gate is the post-turn Channel Delivery Approval.
    public PermissionConfig Permissions { get; init; } = new()
    {
        Mode = "auto",
        RequireApprovalTools = [],
    };

    public double ContextCompressionTriggerRatio { get; init; } = 0.60;

    public double ContextCompressionTargetRatio { get; init; } = 0.40;

    public int DefaultContextWindowSize { get; init; } = 128_000;

    /// <summary>
    /// Number of days of inactivity after which a channel session is reset instead of resumed.
    /// Set to 0 to disable timeout-based reset.
    /// </summary>
    public int SessionTimeoutDays { get; init; } = 7;

    /// <summary>
    /// Number of lines in SUMMARY.md that triggers LLM compression of the oldest portion.
    /// </summary>
    public int SummaryCompressionThreshold { get; init; } = 80;

    /// <summary>
    /// Target number of recent lines to retain after LLM compression.
    /// </summary>
    public int SummaryCompressionTargetLines { get; init; } = 40;

    public bool LlmSummaryEnabled { get; init; } = false;

    /// <summary>
    /// Whether to push intermediate Agent thinking text as channel progress messages during a turn.
    /// When false (default), no progress is streamed and the final reply is delivered only after the turn completes.
    /// </summary>
    public bool EnableProgressStreaming { get; init; } = false;

    /// <summary>
    /// Compression prompt for direct-message (DM) channel sessions.
    /// DM has owner-level trust; the summary should focus on personal task context,
    /// workspace changes, and owner instructions — similar to the main session.
    /// Set to empty string to fall back to the LlmContextSummarizer built-in default.
    /// </summary>
    public string DmCompressionPrompt { get; init; } = ChannelCompressionPrompts.Dm;

    /// <summary>
    /// Compression prompt for group channel sessions.
    /// Group sessions are mention-triggered and multi-participant; the summary should
    /// focus on group context, active participants, and Koda's replies — not file system state.
    /// Set to empty string to fall back to the LlmContextSummarizer built-in default.
    /// </summary>
    public string GroupCompressionPrompt { get; init; } = ChannelCompressionPrompts.Group;

    /// <summary>
    /// Tool-result payload size (serialized bytes) at or above which the result is offloaded
    /// to the artifact store. See <see cref="MainSessionOptions.ToolResultThresholdBytes"/> for details.
    /// </summary>
    public int ToolResultThresholdBytes { get; init; } = 30_000;
}
