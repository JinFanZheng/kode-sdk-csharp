using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Tools.Orchestration.Internal;

namespace Kode.Agent.Tools.Orchestration;

/// <summary>
/// Routes a task to a sub-agent that adopts a specified specialist role via a
/// custom system prompt. Use when the task benefits from a domain-specific
/// perspective (security review, architecture critique, legal compliance check)
/// without polluting the main agent's persona or context.
/// </summary>
[Tool("ask_specialist")]
[ToolAttributes(ReadOnly = true, NoEffect = true)]
public sealed class AskSpecialistTool : OrchestrationToolBase<AskSpecialistArgs>
{
    public AskSpecialistTool(
        IModelProvider modelProvider,
        string modelId,
        IToolRegistry toolRegistry,
        ISandboxFactory sandboxFactory,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
        : base(modelProvider, modelId, toolRegistry, sandboxFactory, loggerFactory)
    {
    }

    public override string Name => "ask_specialist";

    public override string Description =>
        "Run a task in a sub-agent that adopts a named specialist role " +
        "(e.g. 'security engineer', 'database architect') for domain-specific analysis. " +
        "Use spawn_agent instead when the persona + tool whitelist is reusable across calls " +
        "and worth storing in a JSON template.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<AskSpecialistArgs>();

    public override ToolAttributes Attributes => new() { ReadOnly = true, NoEffect = true };

    public override ValueTask<string?> GetPromptAsync(ToolContext context) =>
        ValueTask.FromResult<string?>(
            "State `specialistRole` as a concrete identity — it shapes how the sub-agent reasons. " +
            "Good: 'Senior security engineer auditing auth flows', 'Principal Postgres DBA'. " +
            "Vague roles ('expert', 'reviewer') produce vague analysis. " +
            "The sub-agent sees only the role and task — include any required background in the task itself.");

    protected override async Task<ToolResult> ExecuteAsync(
        AskSpecialistArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (EnsureModelConfigured(Name) is { } missingModel)
            return missingModel;

        if (string.IsNullOrWhiteSpace(args.SpecialistRole))
            return ToolResult.Fail("SpecialistRole must not be empty.");

        var childWorkDir = args.WorkDir ?? context.SandboxOptions?.WorkingDirectory;
        var systemPrompt = BuildSpecialistSystemPrompt(args.SpecialistRole, childWorkDir);

        var result = await SubAgentRunner.RunAsync(CreateBaseRequest(context, args.Task) with
        {
            WorkDir = args.WorkDir,
            Tools = args.Tools,
            MaxIterations = args.MaxIterations,
            MaxContextTokens = args.MaxContextTokens,
            MaxIterationsMode = args.MaxIterationsMode,
            SystemPromptOverride = systemPrompt,
            ParentEventBus = context.Agent?.EventBus,
            Label = args.SpecialistRole,
            ToolCallId = context.CallId,
        }, cancellationToken);

        if (!result.Success)
            return ToolResult.Fail(result.Error ?? "Specialist sub-agent failed.");

        return ToolResult.Ok(new
        {
            summary = result.Summary,
            specialistRole = args.SpecialistRole,
            stopReason = result.StopReason,
        });
    }

    private static string BuildSpecialistSystemPrompt(string role, string? workDir)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are {role}.");

        if (!string.IsNullOrWhiteSpace(workDir))
            sb.AppendLine($"Working directory: {workDir}");

        sb.AppendLine();
        sb.AppendLine("Guidelines:");
        sb.AppendLine("- Apply your specialist expertise to the task. Be systematic and thorough.");
        sb.AppendLine("- Do NOT send messages, modify workspace files outside the task scope, or create approvals.");
        sb.AppendLine("- Produce a concise, expert-level response under 500 words.");
        sb.AppendLine("- If something cannot be determined with the available information, say so explicitly.");
        sb.AppendLine("- Respond in the same language as the task.");

        return sb.ToString();
    }
}
