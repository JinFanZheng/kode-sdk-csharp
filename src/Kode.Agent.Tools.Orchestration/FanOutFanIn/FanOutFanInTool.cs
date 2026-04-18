using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Tools.Orchestration.Internal;

namespace Kode.Agent.Tools.Orchestration;

/// <summary>
/// Runs multiple independent research tasks in parallel (fan-out), collects their
/// summaries, then passes all summaries to a single synthesis sub-agent (fan-in).
///
/// Compared to parallel_research: parallel_research returns raw summaries side-by-side;
/// fan_out_fan_in adds a final synthesis step that produces a single integrated output.
/// Use fan_out_fan_in when you need a unified conclusion, not just parallel summaries.
/// </summary>
[Tool("fan_out_fan_in")]
[ToolAttributes(ReadOnly = true, NoEffect = true)]
public sealed class FanOutFanInTool : OrchestrationToolBase<FanOutFanInArgs>
{
    public FanOutFanInTool(
        IModelProvider modelProvider,
        string modelId,
        IToolRegistry toolRegistry,
        ISandboxFactory sandboxFactory,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : base(modelProvider, modelId, toolRegistry, sandboxFactory, loggerFactory)
    {
    }

    public override string Name => "fan_out_fan_in";

    public override string Description =>
        "Run independent tasks in parallel (fan-out), then pass all summaries to a synthesis sub-agent " +
        "for one integrated output (fan-in). Use parallel_research if raw per-task summaries are enough; " +
        "use map_reduce when the fan-out items are homogeneous chunks of a larger dataset.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<FanOutFanInArgs>();

    public override ToolAttributes Attributes => new() { ReadOnly = true, NoEffect = true };

    public override ValueTask<string?> GetPromptAsync(ToolContext context) =>
        ValueTask.FromResult<string?>(
            "Write `synthesisTask` as a concrete instruction " +
            "(e.g. 'Compare the three candidates and recommend one, with rationale') — " +
            "the synthesis agent sees all fan-out summaries as context. " +
            "Keep each fan-out task narrow; broad fan-outs produce long summaries that crowd synthesis context.");

    protected override async Task<ToolResult> ExecuteAsync(
        FanOutFanInArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (EnsureModelConfigured(Name) is { } missingModel)
            return missingModel;

        if (args.Tasks is not { Count: > 0 })
            return ToolResult.Fail("fan_out_fan_in requires at least one task.");

        if (string.IsNullOrWhiteSpace(args.SynthesisTask))
            return ToolResult.Fail("SynthesisTask must not be empty.");

        // ── fan-out: parallel ─────────────────────────────────────────────
        using var semaphore = args.MaxConcurrency > 0
            ? new SemaphoreSlim(args.MaxConcurrency, args.MaxConcurrency)
            : null;

        var fanOutResults = new SubAgentResult[args.Tasks.Count];
        var fanOutTasks = args.Tasks.Select((t, i) => RunFanOutAsync(
            t, i, args, context, fanOutResults, semaphore, cancellationToken)).ToArray();
        await Task.WhenAll(fanOutTasks);

        // ── fan-in: synthesis ──────────────────────────────────────────
        var synthesisTask = BuildSynthesisTask(args.Tasks, fanOutResults, args.SynthesisTask);
        var synthesisResult = await SubAgentRunner.RunAsync(CreateBaseRequest(context, synthesisTask) with
        {
            Tools = args.SynthesisTools,
            MaxIterations = args.SynthesisMaxIterations,
            MaxContextTokens = args.SynthesisMaxContextTokens,
            MaxIterationsMode = args.SynthesisMaxIterationsMode,
        }, cancellationToken);

        var fanOutSummary = fanOutResults
            .Select((r, i) => new
            {
                name = args.Tasks[i].Name ?? $"Task {i + 1}",
                success = r.Success,
                summary = r.Summary,
                error = r.Error,
            }).ToList();

        return ToolResult.Ok(new
        {
            synthesis = synthesisResult.Success ? synthesisResult.Summary : null,
            synthesisError = synthesisResult.Success ? null : synthesisResult.Error,
            fanOut = fanOutSummary,
            succeeded = synthesisResult.Success,
        });
    }

    private async Task RunFanOutAsync(
        ResearchTask task, int index, FanOutFanInArgs args, ToolContext context,
        SubAgentResult[] results, SemaphoreSlim? semaphore, CancellationToken ct)
    {
        if (semaphore is not null) await semaphore.WaitAsync(ct);
        try
        {
            results[index] = await SubAgentRunner.RunAsync(CreateBaseRequest(context, task.Task) with
            {
                WorkDir = task.WorkDir,
                Tools = task.Tools,
                MaxIterations = task.MaxIterations,
                MaxContextTokens = task.MaxContextTokens,
                MaxIterationsMode = task.MaxIterationsMode,
            }, ct);
        }
        catch (OperationCanceledException)
        {
            results[index] = SubAgentResult.Fail("Cancelled");
        }
        finally
        {
            semaphore?.Release();
        }
    }

    private static string BuildSynthesisTask(
        IReadOnlyList<ResearchTask> tasks,
        SubAgentResult[] results,
        string synthesisTask)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("The following research results were gathered in parallel:");
        sb.AppendLine();

        for (var i = 0; i < tasks.Count; i++)
        {
            var name = tasks[i].Name ?? $"Task {i + 1}";
            sb.AppendLine($"## {name}");
            sb.AppendLine(results[i].Success
                ? results[i].Summary ?? "(no output)"
                : $"(failed: {results[i].Error})");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine(synthesisTask);
        return sb.ToString();
    }
}
