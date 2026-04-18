using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Tools.Orchestration.Internal;

namespace Kode.Agent.Tools.Orchestration;

/// <summary>
/// Runs adversarial argumentation: a proponent sub-agent argues FOR a proposition,
/// an opponent sub-agent argues AGAINST it (seeing the proponent's argument),
/// then a judge sub-agent evaluates both sides and delivers a verdict.
///
/// Multi-round debates run the proponent and opponent alternately, each seeing
/// the other's latest argument before responding.
///
/// Use for high-stakes decisions where a single perspective risks blind spots:
/// architecture selection, risk assessment, significant refactors.
/// </summary>
[Tool("debate")]
[ToolAttributes(ReadOnly = true, NoEffect = true)]
public sealed class DebateTool : OrchestrationToolBase<DebateArgs>
{
    public DebateTool(
        IModelProvider modelProvider,
        string modelId,
        IToolRegistry toolRegistry,
        ISandboxFactory sandboxFactory,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : base(modelProvider, modelId, toolRegistry, sandboxFactory, loggerFactory)
    {
    }

    public override string Name => "debate";

    public override string Description =>
        "Run an adversarial debate: proponent argues FOR, opponent argues AGAINST, judge delivers a verdict. " +
        "Use for high-stakes decisions where a single perspective may miss counter-arguments. " +
        "Use ask_specialist when you only need one expert's view, not a contested one.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<DebateArgs>();

    public override ToolAttributes Attributes => new() { ReadOnly = true, NoEffect = true };

    public override ValueTask<string?> GetPromptAsync(ToolContext context) =>
        ValueTask.FromResult<string?>(
            "Phrase `topic` as a takeable proposition ('We should adopt approach X'), not a question. " +
            "Debaters see only the topic and `contextInfo` — put all relevant constraints there. " +
            "`rounds: 1` is sufficient for most decisions; use 2 for complex trade-offs. " +
            "The verdict is the judge's synthesis, not a vote — read both sides before acting.");

    protected override async Task<ToolResult> ExecuteAsync(
        DebateArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (EnsureModelConfigured(Name) is { } missingModel)
            return missingModel;

        var rounds = Math.Clamp(args.Rounds, 1, 3);
        var debateRounds = new List<object>();

        string? lastProponentArg = null;
        string? lastOpponentArg = null;

        for (var round = 1; round <= rounds; round++)
        {
            // ── proponent ─────────────────────────────────────────────────
            var proponentTask = BuildSideTask(
                "FOR", args.Topic, args.ContextInfo, round, lastOpponentArg);

            var proResult = await RunDebaterAsync(proponentTask, args, context, cancellationToken);
            lastProponentArg = proResult.Success ? proResult.Summary : $"(failed: {proResult.Error})";

            debateRounds.Add(new
            {
                round, side = "proponent",
                success = proResult.Success,
                argument = lastProponentArg,
            });

            // ── opponent ──────────────────────────────────────────────────
            var opponentTask = BuildSideTask(
                "AGAINST", args.Topic, args.ContextInfo, round, lastProponentArg);

            var opResult = await RunDebaterAsync(opponentTask, args, context, cancellationToken);
            lastOpponentArg = opResult.Success ? opResult.Summary : $"(failed: {opResult.Error})";

            debateRounds.Add(new
            {
                round, side = "opponent",
                success = opResult.Success,
                argument = lastOpponentArg,
            });
        }

        // ── judge ─────────────────────────────────────────────────────────
        var judgeTask = BuildJudgeTask(args.Topic, args.ContextInfo, lastProponentArg, lastOpponentArg);
        var judgeResult = await SubAgentRunner.RunAsync(CreateBaseRequest(context, judgeTask) with
        {
            Tools = [],
            AllowNoTools = true,
            MaxIterations = 5,
        }, cancellationToken);

        return ToolResult.Ok(new
        {
            topic = args.Topic,
            verdict = judgeResult.Success ? judgeResult.Summary : null,
            judgeError = judgeResult.Success ? null : judgeResult.Error,
            rounds = debateRounds,
            succeeded = judgeResult.Success,
        });
    }

    private Task<SubAgentResult> RunDebaterAsync(
        string task, DebateArgs args, ToolContext context, CancellationToken ct) =>
        SubAgentRunner.RunAsync(CreateBaseRequest(context, task) with
        {
            Tools = args.Tools is { Count: > 0 } ? args.Tools : [],
            AllowNoTools = true,
            MaxIterations = 8,
        }, ct);

    private static string BuildSideTask(
        string stance, string topic, string? context, int round, string? previousOpponentArg)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are an expert debater. Argue {stance} the following proposition.");
        sb.AppendLine();
        sb.AppendLine($"Proposition: {topic}");

        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.AppendLine();
            sb.AppendLine($"Background context: {context}");
        }

        if (round > 1 && !string.IsNullOrWhiteSpace(previousOpponentArg))
        {
            sb.AppendLine();
            sb.AppendLine("The opposing side argued:");
            sb.AppendLine(previousOpponentArg);
            sb.AppendLine();
            sb.AppendLine("Respond to their argument and reinforce your position.");
        }

        sb.AppendLine();
        sb.AppendLine("Present your strongest arguments. Be concise and specific (under 300 words).");
        sb.AppendLine("Respond in the same language as the proposition.");
        return sb.ToString();
    }

    private static string BuildJudgeTask(
        string topic, string? context, string? proponentArg, string? opponentArg)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are an impartial judge evaluating a debate. Deliver a fair verdict.");
        sb.AppendLine();
        sb.AppendLine($"Proposition: {topic}");

        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.AppendLine();
            sb.AppendLine($"Background context: {context}");
        }

        sb.AppendLine();
        sb.AppendLine("## Proponent's argument (FOR):");
        sb.AppendLine(proponentArg ?? "(no argument)");
        sb.AppendLine();
        sb.AppendLine("## Opponent's argument (AGAINST):");
        sb.AppendLine(opponentArg ?? "(no argument)");
        sb.AppendLine();
        sb.AppendLine("Evaluate both arguments on merit. State:");
        sb.AppendLine("1. Which side made the stronger case and why");
        sb.AppendLine("2. The key considerations a decision-maker should weigh");
        sb.AppendLine("3. Your recommendation (accept / reject / conditional)");
        sb.AppendLine();
        sb.AppendLine("Respond in the same language as the proposition.");
        return sb.ToString();
    }
}
