using Kode.Agent.Sdk.Core.Types;

namespace KodaClaw.Runtime;

public sealed class AutomationSessionOptions
{
    public string Model { get; init; } = "koda-main";

    public string? SystemPrompt { get; init; } = "You are KodaClaw automation assistant.";

    public int MaxIterations { get; init; } = 50;

    public int MaxPromptCharacters { get; init; } = 16000;

    public IReadOnlyList<string> Tools { get; init; } = MainSessionOptions.DefaultTools;

    // Automation sessions run headless; no mid-turn tool approval gates.
    public PermissionConfig Permissions { get; init; } = new()
    {
        Mode = "auto",
        RequireApprovalTools = [],
    };

    public double ContextCompressionTriggerRatio { get; init; } = 0.75;

    public double ContextCompressionTargetRatio { get; init; } = 0.50;

    public int DefaultContextWindowSize { get; init; } = 128_000;

    /// <summary>
    /// Custom system prompt for context compression.
    /// Empty string (default) uses the LlmContextSummarizer built-in prompt.
    /// <br/>
    /// Automation sessions benefit from a prompt that retains: task name, trigger time,
    /// execution steps with success/failure status, output artifacts, and next scheduled run.
    /// Set this field in appsettings or DI configuration to override.
    /// </summary>
    public string CompressionPrompt { get; init; } = AutomationCompressionPrompts.Default;

    /// <summary>
    /// Tool-result payload size (serialized bytes) at or above which the result is offloaded
    /// to the artifact store. See <see cref="MainSessionOptions.ToolResultThresholdBytes"/> for details.
    /// </summary>
    public int ToolResultThresholdBytes { get; init; } = 30_000;
}
