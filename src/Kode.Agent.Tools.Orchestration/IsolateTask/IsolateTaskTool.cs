using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Tools.Orchestration.Internal;

namespace Kode.Agent.Tools.Orchestration;

/// <summary>
/// Runs a self-contained investigation task in an isolated sub-agent whose entire
/// message history is discarded afterwards. Only the final summary returns to the
/// parent agent, protecting the parent's context window from large intermediate results.
///
/// Design references:
///   - Anthropic "Building effective agents" — Orchestrator-Worker pattern
///   - MemGPT (arxiv 2310.08560) — hierarchical context management
/// </summary>
[Tool("isolate_task")]
[ToolAttributes(ReadOnly = true, NoEffect = true)]
public sealed class IsolateTaskTool : OrchestrationToolBase<IsolateTaskArgs>
{
    public IsolateTaskTool(
        IModelProvider modelProvider,
        string modelId,
        IToolRegistry toolRegistry,
        ISandboxFactory sandboxFactory,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : base(modelProvider, modelId, toolRegistry, sandboxFactory, loggerFactory)
    {
    }

    public override string Name => "isolate_task";

    public override string Description =>
        "Run a deep-research task in an isolated sub-agent; only the final summary returns, " +
        "protecting the parent's context from large intermediate tool results. " +
        "Prefer ask_specialist when the task needs a specific expert lens, " +
        "parallel_research when several independent investigations can run at once.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<IsolateTaskArgs>();

    public override ToolAttributes Attributes => new() { ReadOnly = true, NoEffect = true };

    public override ValueTask<string?> GetPromptAsync(ToolContext context) =>
        ValueTask.FromResult<string?>(
            "Ask for exactly what you need in the task description — the result is a plain-text summary, " +
            "not structured data. Whitelist only the tools the sub-agent actually needs via `tools`. " +
            "Set `workDir` only when the task targets a directory outside the current workspace. " +
            "The sub-agent cannot send messages, modify workspace, or create approvals.");

    protected override async Task<ToolResult> ExecuteAsync(
        IsolateTaskArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (EnsureModelConfigured(Name) is { } missingModel)
            return missingModel;

        var result = await SubAgentRunner.RunAsync(CreateBaseRequest(context, args.Task) with
        {
            WorkDir = args.WorkDir,
            Tools = args.Tools,
            MaxIterations = args.MaxIterations,
            MaxContextTokens = args.MaxContextTokens,
            MaxIterationsMode = args.MaxIterationsMode,
            ParentEventBus = context.Agent?.EventBus,
            Label = "isolate_task",
            ToolCallId = context.CallId,
        }, cancellationToken);

        if (!result.Success)
            return ToolResult.Fail(result.Error ?? "Sub-agent failed.");

        return ToolResult.Ok(new
        {
            summary = result.Summary,
            stopReason = result.StopReason,
            toolsUsed = result.ToolsUsed,
        });
    }
}
