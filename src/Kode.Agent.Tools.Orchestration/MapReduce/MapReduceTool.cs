using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Tools.Orchestration.Internal;

namespace Kode.Agent.Tools.Orchestration;

/// <summary>
/// Splits a list of items into chunks, processes each chunk in a parallel map
/// sub-agent, then passes all map results to a single reduce sub-agent for aggregation.
///
/// Use when you have a large homogeneous dataset (memory files, log entries, code modules)
/// that exceeds a single sub-agent's context but each piece can be processed independently.
/// </summary>
[Tool("map_reduce")]
[ToolAttributes(ReadOnly = true, NoEffect = true)]
public sealed class MapReduceTool : OrchestrationToolBase<MapReduceArgs>
{
    public MapReduceTool(
        IModelProvider modelProvider,
        string modelId,
        IToolRegistry toolRegistry,
        ISandboxFactory sandboxFactory,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : base(modelProvider, modelId, toolRegistry, sandboxFactory, loggerFactory)
    {
    }

    public override string Name => "map_reduce";

    public override string Description =>
        "Split a list of items into chunks, process each chunk in a parallel map sub-agent, " +
        "then aggregate via a reduce sub-agent. Use for large homogeneous datasets " +
        "(100+ memory files, log sets, code modules) that exceed one context window. " +
        "Use fan_out_fan_in when items are heterogeneous and each needs a distinct task.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<MapReduceArgs>();

    public override ToolAttributes Attributes => new() { ReadOnly = true, NoEffect = true };

    public override ValueTask<string?> GetPromptAsync(ToolContext context) =>
        ValueTask.FromResult<string?>(
            "`mapTask` must contain the `{item}` placeholder for chunk content — without it, the chunk is never inserted. " +
            "Raise `chunkSize` for short items (e.g. 10 log lines per call), keep at 1 for long ones (full files). " +
            "Write `reduceTask` as a concrete aggregation goal ('list all error categories', 'rank by severity') " +
            "rather than another 'summarize'.");

    protected override async Task<ToolResult> ExecuteAsync(
        MapReduceArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (EnsureModelConfigured(Name) is { } missingModel)
            return missingModel;

        if (args.Items is not { Count: > 0 })
            return ToolResult.Fail("Items must not be empty.");

        if (string.IsNullOrWhiteSpace(args.MapTask))
            return ToolResult.Fail("MapTask must not be empty.");

        if (string.IsNullOrWhiteSpace(args.ReduceTask))
            return ToolResult.Fail("ReduceTask must not be empty.");

        var chunkSize = Math.Max(1, args.ChunkSize);
        var chunks = args.Items
            .Select((item, i) => (item, i))
            .GroupBy(x => x.i / chunkSize)
            .Select(g => g.Select(x => x.item).ToList())
            .ToList();

        // ── map phase ─────────────────────────────────────────────────────
        using var semaphore = args.MaxConcurrency > 0
            ? new SemaphoreSlim(args.MaxConcurrency, args.MaxConcurrency)
            : null;

        var mapResults = new SubAgentResult[chunks.Count];
        var mapTasks = chunks.Select((chunk, i) =>
            RunMapAsync(chunk, i, args, context, mapResults, semaphore, cancellationToken)
        ).ToArray();
        await Task.WhenAll(mapTasks);

        // ── reduce phase ──────────────────────────────────────────────
        var reduceTask = BuildReduceTask(mapResults, args.ReduceTask);
        var reduceResult = await SubAgentRunner.RunAsync(CreateBaseRequest(context, reduceTask) with
        {
            Tools = null,        // reduce is typically reasoning-only; use defaults
            MaxIterations = args.ReduceMaxIterations,
            MaxContextTokens = args.ReduceMaxContextTokens,
            MaxIterationsMode = args.ReduceMaxIterationsMode,
        }, cancellationToken);

        var mapSummary = mapResults.Select((r, i) => new
        {
            chunk = i,
            items = chunks[i],
            success = r.Success,
            summary = r.Summary,
            error = r.Error,
        }).ToList();

        return ToolResult.Ok(new
        {
            reduction = reduceResult.Success ? reduceResult.Summary : null,
            reductionError = reduceResult.Success ? null : reduceResult.Error,
            totalItems = args.Items.Count,
            totalChunks = chunks.Count,
            mapResults = mapSummary,
            succeeded = reduceResult.Success,
        });
    }

    private async Task RunMapAsync(
        List<string> chunk, int index, MapReduceArgs args, ToolContext context,
        SubAgentResult[] results, SemaphoreSlim? semaphore, CancellationToken ct)
    {
        if (semaphore is not null) await semaphore.WaitAsync(ct);
        try
        {
            var itemContent = chunk.Count == 1
                ? chunk[0]
                : string.Join("\n---\n", chunk.Select((item, i) => $"Item {i + 1}:\n{item}"));

            var task = args.MapTask.Replace("{item}", itemContent, StringComparison.OrdinalIgnoreCase);

            results[index] = await SubAgentRunner.RunAsync(CreateBaseRequest(context, task) with
            {
                Tools = args.MapTools,
                MaxIterations = args.MapMaxIterations,
                MaxContextTokens = args.MapMaxContextTokens,
                MaxIterationsMode = args.MapMaxIterationsMode,
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

    private static string BuildReduceTask(SubAgentResult[] mapResults, string reduceTask)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Map phase results:");
        sb.AppendLine();

        for (var i = 0; i < mapResults.Length; i++)
        {
            sb.AppendLine($"## Chunk {i + 1}");
            sb.AppendLine(mapResults[i].Success
                ? mapResults[i].Summary ?? "(no output)"
                : $"(failed: {mapResults[i].Error})");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine(reduceTask);
        return sb.ToString();
    }
}
