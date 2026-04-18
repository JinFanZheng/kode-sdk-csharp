using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Tools.Orchestration.Internal;

namespace Kode.Agent.Tools.Orchestration;

/// <summary>
/// Passes large content to a sub-agent for distillation, returning only the
/// key information relevant to a focus question. No tool calls are made —
/// the sub-agent reasons over the provided text purely in-context.
///
/// Use when a tool result, file content, or accumulated context is too large
/// to pass directly to the next step in a pipeline or decision.
/// </summary>
[Tool("context_distill")]
[ToolAttributes(ReadOnly = true, NoEffect = true)]
public sealed class ContextDistillTool : OrchestrationToolBase<ContextDistillArgs>
{
    public ContextDistillTool(
        IModelProvider modelProvider,
        string modelId,
        IToolRegistry toolRegistry,
        ISandboxFactory sandboxFactory,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : base(modelProvider, modelId, toolRegistry, sandboxFactory, loggerFactory)
    {
    }

    public override string Name => "context_distill";

    public override string Description =>
        "Pass large text (tool output, file dumps, logs) and a focus question to a sub-agent; " +
        "returns only the distilled content relevant to the question. " +
        "Pure text reasoning — no tool calls. Use isolate_task if the distillation needs live file reads.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<ContextDistillArgs>();

    public override ToolAttributes Attributes => new() { ReadOnly = true, NoEffect = true };

    public override ValueTask<string?> GetPromptAsync(ToolContext context) =>
        ValueTask.FromResult<string?>(
            "`focusQuestion` must be specific ('What env vars does this config reference?' — not 'summarize this'). " +
            "Tune `maxOutputWords` to the downstream step's need: too tight drops detail, too loose wastes tokens. " +
            "Use this for extraction and summarization, not structured transformation.");

    protected override async Task<ToolResult> ExecuteAsync(
        ContextDistillArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (EnsureModelConfigured(Name) is { } missingModel)
            return missingModel;

        if (string.IsNullOrWhiteSpace(args.Content))
            return ToolResult.Fail("Content must not be empty.");

        if (string.IsNullOrWhiteSpace(args.FocusQuestion))
            return ToolResult.Fail("FocusQuestion must not be empty.");

        var maxWords = Math.Clamp(args.MaxOutputWords, 50, 1000);
        var task = BuildDistillTask(args.Content, args.FocusQuestion, maxWords);

        var result = await SubAgentRunner.RunAsync(CreateBaseRequest(context, task) with
        {
            Tools = [],              // no tool calls — pure reasoning
            AllowNoTools = true,
            MaxIterations = 3,       // single response expected
        }, cancellationToken);

        if (!result.Success)
            return ToolResult.Fail(result.Error ?? "Distillation sub-agent failed.");

        return ToolResult.Ok(new
        {
            distillation = result.Summary,
            focusQuestion = args.FocusQuestion,
        });
    }

    private static string BuildDistillTask(string content, string focusQuestion, int maxWords) =>
        $"""
         You are a precise content distiller.

         Focus question: {focusQuestion}

         Content to distill:
         ---
         {content}
         ---

         Extract and summarise only the information relevant to the focus question above.
         Keep your response under {maxWords} words. Be specific and factual. Omit anything unrelated.
         Respond in the same language as the focus question.
         """;
}
