using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Tools.Orchestration.Internal;

namespace Kode.Agent.Tools.Orchestration;

/// <summary>
/// Runs a task in an isolated sub-agent and, on failure, retries with the error
/// and a reflection prompt prepended — asking the sub-agent to diagnose what went
/// wrong and approach the problem differently.
///
/// Ideal for unattended HEARTBEAT automation stages where transient errors
/// (wrong path, unexpected output format, minor tool misuse) should self-correct
/// rather than abort the entire pipeline.
///
/// Design reference: Anthropic "Building effective agents" — Reflection pattern
/// </summary>
[Tool("retry_with_reflection")]
[ToolAttributes(ReadOnly = true, NoEffect = true)]
public sealed class RetryWithReflectionTool : OrchestrationToolBase<RetryWithReflectionArgs>
{
    public RetryWithReflectionTool(
        IModelProvider modelProvider,
        string modelId,
        IToolRegistry toolRegistry,
        ISandboxFactory sandboxFactory,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : base(modelProvider, modelId, toolRegistry, sandboxFactory, loggerFactory)
    {
    }

    public override string Name => "retry_with_reflection";

    public override string Description =>
        "Run a task in a sub-agent and retry on failure; each retry receives the previous error " +
        "plus a reflection prompt to approach the problem differently. " +
        "Use for transient errors (wrong path, format drift, minor tool misuse). " +
        "Use validate_and_fix when success is defined by explicit quality criteria, not just 'didn't crash'.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<RetryWithReflectionArgs>();

    public override ToolAttributes Attributes => new() { ReadOnly = true, NoEffect = true };

    public override ValueTask<string?> GetPromptAsync(ToolContext context) =>
        ValueTask.FromResult<string?>(
            "`maxRetries: 1` for quick tasks, 2–3 for complex ones (clamped to 5). " +
            "All attempt records are returned even on final failure — inspect them to diagnose systemic issues. " +
            "If attempts keep failing the same way, the task description is wrong; don't just raise retries.");

    protected override async Task<ToolResult> ExecuteAsync(
        RetryWithReflectionArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (EnsureModelConfigured(Name) is { } missingModel)
            return missingModel;

        var totalAttempts = Math.Clamp(args.MaxRetries, 0, 5) + 1;
        var attemptRecords = new List<object>(totalAttempts);
        string? previousError = null;

        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            var task = attempt == 1
                ? args.Task
                : BuildRetryTask(args.Task, attempt - 1, previousError!);

            var result = await SubAgentRunner.RunAsync(CreateBaseRequest(context, task) with
            {
                WorkDir = args.WorkDir,
                Tools = args.Tools,
                MaxIterations = args.MaxIterationsPerAttempt,
                MaxContextTokens = args.MaxContextTokens,
                MaxIterationsMode = args.MaxIterationsMode,
                ParentEventBus = context.Agent?.EventBus,
                Label = $"retry_with_reflection:{attempt}/{totalAttempts}",
                ToolCallId = context.CallId,
            }, cancellationToken);

            if (result.Success)
            {
                attemptRecords.Add(new { attempt, success = true, summary = result.Summary });
                // Return Ok (not Fail) even here so the caller gets full attempt history
                return ToolResult.Ok(new
                {
                    success = true,
                    summary = result.Summary,
                    totalAttempts = attempt,
                    attempts = attemptRecords,
                });
            }

            previousError = result.Error;
            attemptRecords.Add(new { attempt, success = false, error = result.Error });
        }

        // All attempts exhausted — return Ok with success:false so the caller can inspect
        // the full attempt history (tests assert on ToolResult.Success==true + success:false payload).
        return ToolResult.Ok(new
        {
            success = false,
            error = previousError ?? "unknown error",
            totalAttempts,
            attempts = attemptRecords,
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string BuildRetryTask(string originalTask, int failedAttempt, string error) =>
        $"""
         Original task:
         {originalTask}

         Attempt {failedAttempt} failed with the following error:
         {error}

         Before retrying, reflect on what went wrong:
         - Was the approach correct? Were the right tools used?
         - Was there a wrong assumption about file paths, formats, or tool behaviour?
         - What should change this time to avoid the same failure?

         Now retry the original task with this understanding.
         Respond in the same language as the original task.
         """;
}
