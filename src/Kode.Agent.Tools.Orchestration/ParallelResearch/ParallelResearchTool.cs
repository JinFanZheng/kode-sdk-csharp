using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Tools.Orchestration.Internal;

namespace Kode.Agent.Tools.Orchestration;

/// <summary>
/// Executes multiple research tasks in parallel, each in an isolated sub-agent.
/// All sub-agents run concurrently (or up to <see cref="ParallelResearchArgs.MaxConcurrency"/>
/// at a time), and their summaries are collected and returned together.
///
/// Ideal for HEARTBEAT Stage 1 (gather) and memory consolidation, where multiple
/// independent topics need investigation simultaneously.
///
/// Design reference: Anthropic "Building effective agents" — Parallelization pattern
/// </summary>
[Tool("parallel_research")]
[ToolAttributes(ReadOnly = true, NoEffect = true)]
public sealed class ParallelResearchTool : OrchestrationToolBase<ParallelResearchArgs>
{
    public ParallelResearchTool(
        IModelProvider modelProvider,
        string modelId,
        IToolRegistry toolRegistry,
        ISandboxFactory sandboxFactory,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : base(modelProvider, modelId, toolRegistry, sandboxFactory, loggerFactory)
    {
    }

    public override string Name => "parallel_research";

    public override string Description =>
        "Run multiple independent research tasks concurrently in isolated sub-agents; " +
        "returns raw per-task summaries side-by-side. " +
        "Use fan_out_fan_in instead when you need one integrated conclusion, " +
        "pipeline when later tasks depend on earlier results.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<ParallelResearchArgs>();

    public override ToolAttributes Attributes => new() { ReadOnly = true, NoEffect = true };

    public override ValueTask<string?> GetPromptAsync(ToolContext context) =>
        ValueTask.FromResult<string?>(
            "Only use when tasks are truly independent — shared dependencies belong in pipeline. " +
            "Prefer `fan_out_fan_in` when (a) you have 5+ tasks, or (b) you ultimately need a single integrated conclusion rather than N side-by-side reports — " +
            "its synthesis sub-agent keeps the aggregated output bounded, while parallel_research returns raw per-task summaries that scale linearly with task count. " +
            "Cap `maxConcurrency` (e.g. 3–5) to control API spend when investigating many topics. " +
            "Set `failFast: true` when one failure invalidates the rest; otherwise collect partial results. " +
            "Sub-agents cannot send messages, modify workspace, or create approvals.");

    protected override async Task<ToolResult> ExecuteAsync(
        ParallelResearchArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (EnsureModelConfigured(Name) is { } missingModel)
            return missingModel;

        if (args.Tasks is not { Count: > 0 })
            return ToolResult.Fail("parallel_research requires at least one task.");

        if (args.MaxConcurrency < 0)
            return ToolResult.Fail("MaxConcurrency must be 0 (unlimited) or a positive number.");

        // Linked token: FailFast cancels remaining tasks on first failure
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linkedCts.Token;

        using var semaphore = args.MaxConcurrency > 0
            ? new SemaphoreSlim(args.MaxConcurrency, args.MaxConcurrency)
            : null;

        // Launch all tasks, collecting results into a pre-sized array to preserve order
        var resultSlots = new TaskResultSlot[args.Tasks.Count];
        var runningTasks = args.Tasks.Select((t, i) =>
            RunOneAsync(t, i, args, context, resultSlots, linkedCts, semaphore, token)
        ).ToArray();

        // WhenAll collects all, even if some fail (exceptions are surfaced after all complete)
        await Task.WhenAll(runningTasks);

        return BuildToolResult(resultSlots, args.Tasks.Count);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task RunOneAsync(
        ResearchTask task,
        int index,
        ParallelResearchArgs args,
        ToolContext context,
        TaskResultSlot[] resultSlots,
        CancellationTokenSource linkedCts,
        SemaphoreSlim? semaphore,
        CancellationToken token)
    {
        var taskName = task.Name ?? $"Task {index + 1}";

        // Check for cancellation before acquiring semaphore (FailFast path)
        if (token.IsCancellationRequested)
        {
            resultSlots[index] = TaskResultSlot.Cancelled(taskName);
            return;
        }

        if (semaphore is not null)
            await semaphore.WaitAsync(token);

        try
        {
            if (token.IsCancellationRequested)
            {
                resultSlots[index] = TaskResultSlot.Cancelled(taskName);
                return;
            }

            var result = await SubAgentRunner.RunAsync(CreateBaseRequest(context, task.Task) with
            {
                WorkDir = task.WorkDir,
                Tools = task.Tools,
                MaxIterations = task.MaxIterations,
                MaxContextTokens = task.MaxContextTokens,
                MaxIterationsMode = task.MaxIterationsMode,
            }, token);

            resultSlots[index] = result.Success
                ? TaskResultSlot.Ok(taskName, result.Summary, result.StopReason)
                : TaskResultSlot.Failed(taskName, result.Error);

            // FailFast: cancel siblings on first failure
            if (!result.Success && args.FailFast)
                linkedCts.Cancel();
        }
        catch (OperationCanceledException)
        {
            resultSlots[index] = TaskResultSlot.Cancelled(taskName);
        }
        finally
        {
            semaphore?.Release();
        }
    }

    private static ToolResult BuildToolResult(TaskResultSlot[] slots, int total)
    {
        var results = slots.Select(s => (object)new
        {
            name = s.Name,
            success = s.Success,
            summary = s.Summary,
            stopReason = s.StopReason,
            error = s.Error,
        }).ToList();

        var successCount = slots.Count(s => s.Success);
        var failureCount = total - successCount;

        return ToolResult.Ok(new
        {
            results,
            successCount,
            failureCount,
            totalTasks = total,
            succeeded = failureCount == 0,
        });
    }

    // ── result slot (mutable, array-indexed for ordering) ────────────────────

    private sealed class TaskResultSlot
    {
        public string Name { get; private init; } = "";
        public bool Success { get; private init; }
        public string? Summary { get; private init; }
        public string? StopReason { get; private init; }
        public string? Error { get; private init; }

        public static TaskResultSlot Ok(string name, string? summary, string? stopReason) => new()
        {
            Name = name, Success = true, Summary = summary, StopReason = stopReason,
        };

        public static TaskResultSlot Failed(string name, string? error) => new()
        {
            Name = name, Success = false, Error = error,
        };

        public static TaskResultSlot Cancelled(string name) => new()
        {
            Name = name, Success = false, Error = "Cancelled due to FailFast",
        };
    }
}
