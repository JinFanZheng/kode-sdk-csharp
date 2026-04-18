using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Tools.Orchestration.Internal;

namespace Kode.Agent.Tools.Orchestration;

/// <summary>
/// Executes a multi-stage pipeline where each stage runs in an isolated sub-agent.
/// The summary from each stage is passed as context to the next stage, so only
/// refined summaries accumulate — not raw intermediate tool results.
///
/// Ideal for HEARTBEAT-style automation workflows where multiple sequential phases
/// (e.g. gather → consolidate → clean) would otherwise pollute a single context window.
///
/// Design reference: Anthropic "Building effective agents" — Pipeline pattern
/// </summary>
[Tool("pipeline")]
[ToolAttributes(ReadOnly = false, NoEffect = false)]
public sealed class PipelineTool : OrchestrationToolBase<PipelineArgs>
{
    public PipelineTool(
        IModelProvider modelProvider,
        string modelId,
        IToolRegistry toolRegistry,
        ISandboxFactory sandboxFactory,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : base(modelProvider, modelId, toolRegistry, sandboxFactory, loggerFactory)
    {
    }

    public override string Name => "pipeline";

    public override string Description =>
        "Run sequential stages where each stage is an isolated sub-agent and only its summary " +
        "flows to the next. Use for multi-phase workflows (gather → analyze → report) that would " +
        "otherwise overflow one context. Use parallel_research when stages are independent, " +
        "isolate_task for a single deep-dive.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<PipelineArgs>();

    public override ToolAttributes Attributes => new() { ReadOnly = false, NoEffect = false };

    public override ValueTask<string?> GetPromptAsync(ToolContext context) =>
        ValueTask.FromResult<string?>(
            "Each stage's summary is the only context the next stage gets — state in the task " +
            "exactly what downstream stages need (file paths, key findings, not raw dumps). " +
            "Give each stage a descriptive `name` so the handoff header is informative. " +
            "Set `stopOnFailure: true` to halt on first failure; otherwise later stages run with partial context.");

    protected override async Task<ToolResult> ExecuteAsync(
        PipelineArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (EnsureModelConfigured(Name) is { } missingModel)
            return missingModel;

        if (args.Stages is not { Count: > 0 })
            return ToolResult.Fail("Pipeline must have at least one stage.");

        var stageResults = new List<object>();
        string? previousSummary = null;
        string? previousName = null;
        var completedCount = 0;

        for (var i = 0; i < args.Stages.Count; i++)
        {
            var stage = args.Stages[i];
            var stageName = stage.Name ?? $"Stage {i + 1}";

            // Prepend previous stage summary as context
            var task = previousSummary is not null
                ? $"Context from previous stage ({previousName}):\n{previousSummary}\n\n{stage.Task}"
                : stage.Task;

            var result = await SubAgentRunner.RunAsync(CreateBaseRequest(context, task) with
            {
                WorkDir = stage.WorkDir,
                Tools = stage.Tools,
                MaxIterations = stage.MaxIterations,
                MaxContextTokens = stage.MaxContextTokens,
                MaxIterationsMode = stage.MaxIterationsMode,
                ParentEventBus = context.Agent?.EventBus,
                Label = $"pipeline:{stageName}",
                ToolCallId = context.CallId,
            }, cancellationToken);

            if (result.Success)
            {
                completedCount++;
                previousSummary = result.Summary;
                previousName = stageName;

                stageResults.Add(new
                {
                    name = stageName,
                    success = true,
                    summary = result.Summary,
                    stopReason = result.StopReason,
                });
            }
            else
            {
                stageResults.Add(new
                {
                    name = stageName,
                    success = false,
                    summary = (string?)null,
                    error = result.Error,
                });

                if (args.StopOnFailure)
                    break;
            }
        }

        var allSucceeded = completedCount == args.Stages.Count;

        return ToolResult.Ok(new
        {
            stages = stageResults,
            completedStages = completedCount,
            totalStages = args.Stages.Count,
            succeeded = allSucceeded,
        });
    }
}
