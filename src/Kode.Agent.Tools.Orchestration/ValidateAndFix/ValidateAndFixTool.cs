using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Tools.Orchestration.Internal;

namespace Kode.Agent.Tools.Orchestration;

/// <summary>
/// Executes a task, validates the output against explicit criteria using a separate
/// validator sub-agent, then if validation fails, runs a fix sub-agent with the
/// feedback — repeating up to MaxFixRounds times.
///
/// Unlike retry_with_reflection (which retries on agent failure), validate_and_fix
/// retries on logical/quality failure determined by structured external criteria.
/// </summary>
[Tool("validate_and_fix")]
[ToolAttributes(ReadOnly = false, NoEffect = false)]
public sealed class ValidateAndFixTool : OrchestrationToolBase<ValidateAndFixArgs>
{
    public ValidateAndFixTool(
        IModelProvider modelProvider,
        string modelId,
        IToolRegistry toolRegistry,
        ISandboxFactory sandboxFactory,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : base(modelProvider, modelId, toolRegistry, sandboxFactory, loggerFactory)
    {
    }

    public override string Name => "validate_and_fix";

    public override string Description =>
        "Run a task, validate its output against explicit criteria, and if it fails hand the output " +
        "plus validator feedback to a fix sub-agent — up to MaxFixRounds times. " +
        "Use when output must meet structured quality rules (compiles, contains required sections). " +
        "Use retry_with_reflection when 'success' is just the task not crashing.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<ValidateAndFixArgs>();

    public override ToolAttributes Attributes => new() { ReadOnly = false, NoEffect = false };

    public override ValueTask<string?> GetPromptAsync(ToolContext context) =>
        ValueTask.FromResult<string?>(
            "Write `validationCriteria` as concrete pass/fail rules, not vague preferences " +
            "('compiles cleanly', 'includes a Risks section' — not 'is good'). " +
            "Ambiguous criteria always pass. Keep `maxFixRounds` low (2–3, clamped to 4); " +
            "if it can't be fixed in 3 rounds, the criteria or the task are wrong.");

    protected override async Task<ToolResult> ExecuteAsync(
        ValidateAndFixArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (EnsureModelConfigured(Name) is { } missingModel)
            return missingModel;

        if (string.IsNullOrWhiteSpace(args.ValidationCriteria))
            return ToolResult.Fail("ValidationCriteria must not be empty.");

        var maxRounds = Math.Clamp(args.MaxFixRounds, 0, 4);
        var rounds = new List<object>();

        // ── initial execution ──────────────────────────────────────────────
        var execResult = await RunSubAgent(args.Task, args, context, "execute", cancellationToken);
        if (!execResult.Success)
        {
            rounds.Add(new { round = 0, stage = "execute", passed = false, error = execResult.Error });
            return ToolResult.Ok(new { passed = false, output = (string?)null, rounds });
        }

        var currentOutput = execResult.Summary!;

        for (var round = 0; round <= maxRounds; round++)
        {
            // ── validate (pure-reasoning, no event forwarding) ─────────────
            var validationTask = BuildValidationTask(currentOutput, args.ValidationCriteria);
            var validResult = await SubAgentRunner.RunAsync(CreateBaseRequest(context, validationTask) with
            {
                Tools = [],
                AllowNoTools = true,
                MaxIterations = 3,
            }, cancellationToken);

            var validSummary = validResult.Success ? validResult.Summary ?? "" : "";
            var passed = validSummary.Contains("PASS", StringComparison.OrdinalIgnoreCase)
                         && !validSummary.Contains("FAIL", StringComparison.OrdinalIgnoreCase);

            rounds.Add(new { round, stage = "validate", passed, feedback = validSummary });

            if (passed)
                return ToolResult.Ok(new { passed = true, output = currentOutput, rounds });

            if (round >= maxRounds)
                break;

            // ── fix ────────────────────────────────────────────────────────
            var fixTask = BuildFixTask(args.Task, currentOutput, validSummary);
            var fixResult = await RunSubAgent(fixTask, args, context, $"fix:{round + 1}", cancellationToken);

            rounds.Add(new { round, stage = "fix", success = fixResult.Success, error = fixResult.Error });

            if (!fixResult.Success)
                break;

            currentOutput = fixResult.Summary!;
        }

        return ToolResult.Ok(new { passed = false, output = currentOutput, rounds });
    }

    private Task<SubAgentResult> RunSubAgent(
        string task, ValidateAndFixArgs args, ToolContext context, string label, CancellationToken ct) =>
        SubAgentRunner.RunAsync(CreateBaseRequest(context, task) with
        {
            WorkDir = args.WorkDir,
            Tools = args.Tools,
            MaxIterations = args.MaxIterationsPerAttempt,
            MaxContextTokens = args.MaxContextTokens,
            MaxIterationsMode = args.MaxIterationsMode,
            ParentEventBus = context.Agent?.EventBus,
            Label = $"validate_and_fix:{label}",
            ToolCallId = context.CallId,
        }, ct);

    private static string BuildValidationTask(string output, string criteria) =>
        $"""
         You are a strict output validator. Evaluate the output below against the criteria.
         Respond with exactly one of:
           PASS — if all criteria are satisfied
           FAIL: <brief reason> — if any criterion is not satisfied

         Use English keywords PASS / FAIL verbatim; the reason may be in any language.

         Criteria:
         {criteria}

         Output to validate:
         ---
         {output}
         ---
         """;

    private static string BuildFixTask(string originalTask, string currentOutput, string validatorFeedback) =>
        $"""
         Original task:
         {originalTask}

         Your previous output failed validation with this feedback:
         {validatorFeedback}

         Previous output:
         ---
         {currentOutput}
         ---

         Fix the output to satisfy the validation criteria. Produce the corrected result directly.
         Respond in the same language as the original task.
         """;
}
