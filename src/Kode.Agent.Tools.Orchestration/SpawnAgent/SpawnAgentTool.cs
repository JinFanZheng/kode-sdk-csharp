using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.Tools.Orchestration.Internal;

namespace Kode.Agent.Tools.Orchestration;

/// <summary>
/// Spawns a sub-agent based on a JSON template file.
/// The template defines the sub-agent's system prompt, allowed tools, model, and runtime configuration.
/// </summary>
[Tool("spawn_agent")]
[ToolAttributes(ReadOnly = false, NoEffect = false)]
public sealed class SpawnAgentTool : OrchestrationToolBase<SpawnAgentArgs>
{
    private readonly IReadOnlyList<string>? _skillsPaths;

    public SpawnAgentTool(
        IModelProvider modelProvider,
        string modelId,
        IToolRegistry toolRegistry,
        ISandboxFactory sandboxFactory,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
        IReadOnlyList<string>? skillsPaths = null)
        : base(modelProvider, modelId, toolRegistry, sandboxFactory, loggerFactory)
    {
        _skillsPaths = skillsPaths;
    }

    public override string Name => "spawn_agent";

    public override string Description =>
        "Launch a sub-agent from a JSON template file that defines its system prompt, allowed tools, " +
        "and runtime config. Use for reusable agent personas stored on disk. " +
        "Prefer ask_specialist for a one-off expert perspective that doesn't need a template.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<SpawnAgentArgs>();

    public override ToolAttributes Attributes => new() { ReadOnly = false, NoEffect = false };

    public override ValueTask<string?> GetPromptAsync(ToolContext context) =>
        ValueTask.FromResult<string?>(
            "`templatePath` must reference an existing `.json` file with required `id` and `systemPrompt` fields, " +
            "plus optional `tools` / `runtime` sections. The sub-agent inherits only the template's tool list, " +
            "not the parent's. Override runtime limits via `maxIterations` / `maxContextTokens` for heavy tasks.");

    protected override async Task<ToolResult> ExecuteAsync(
        SpawnAgentArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (EnsureModelConfigured(Name) is { } missingModel)
            return missingModel;

        // ── Step 1: Resolve path (L6) ─────────────────────────────────────────
        var baseDir = context.SandboxOptions?.WorkingDirectory ?? Directory.GetCurrentDirectory();
        var resolvedPath = Path.IsPathRooted(args.TemplatePath)
            ? args.TemplatePath
            : Path.GetFullPath(args.TemplatePath, baseDir);

        // ── Step 2: Parse template eagerly (L5) ───────────────────────────────
        LoadedTemplate template;
        try
        {
            template = TemplateFileLoader.LoadFromFile(resolvedPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            return ToolResult.Fail($"Failed to load template from '{resolvedPath}': {ex.Message}");
        }

        // ── Step 3: Run sub-agent ─────────────────────────────────────────────
        var parentEventBus = context.Agent?.EventBus;

        var result = await TemplateAgentRunner.RunAsync(new TemplateRunRequest
        {
            Template = template,
            Prompt = args.Prompt,
            WorkDir = args.WorkDir,
            MaxIterationsOverride = args.MaxIterations,
            MaxContextTokensOverride = args.MaxContextTokens,
            MaxIterationsMode = args.MaxIterationsMode,
            ModelProvider = ModelProvider,
            ModelId = ModelId,
            ToolRegistry = ToolRegistry,
            SandboxFactory = SandboxFactory,
            LoggerFactory = LoggerFactory,
            ParentSandboxOptions = context.SandboxOptions,
            ParentAgentId = context.AgentId,
            ToolCallId = context.CallId,
            ParentEventBus = parentEventBus,
            SkillsPaths = _skillsPaths,
        }, cancellationToken);

        if (!result.Success)
            return ToolResult.Fail(result.Error ?? $"spawn_agent '{template.Definition.Id}' failed.");

        // ── Step 4: Return result with token usage (L7) ───────────────────────
        return ToolResult.Ok(new
        {
            summary = result.Summary,
            templateId = template.Definition.Id,
            stopReason = result.StopReason,
            tokenUsage = result.TokenUsage == null ? null : new
            {
                input = result.TokenUsage.InputTokens,
                output = result.TokenUsage.OutputTokens,
                total = result.TokenUsage.TotalTokens,
            },
            toolsUsed = result.ToolsUsed,
        });
    }
}
