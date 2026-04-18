using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.Tools.Orchestration;

/// <summary>
/// Shared base for orchestration tools that spawn sub-agents via
/// <see cref="Internal.SubAgentRunner"/>.
/// Centralizes the five common dependencies, the model-id guard, and the
/// request factory to keep individual tool classes focused on their strategy.
/// </summary>
public abstract class OrchestrationToolBase<TArgs> : ToolBase<TArgs> where TArgs : class
{
    protected IModelProvider ModelProvider { get; }
    protected string ModelId { get; }
    protected IToolRegistry ToolRegistry { get; }
    protected ISandboxFactory SandboxFactory { get; }
    protected ILoggerFactory? LoggerFactory { get; }

    protected OrchestrationToolBase(
        IModelProvider modelProvider,
        string modelId,
        IToolRegistry toolRegistry,
        ISandboxFactory sandboxFactory,
        ILoggerFactory? loggerFactory)
    {
        ModelProvider = modelProvider;
        ModelId = modelId;
        ToolRegistry = toolRegistry;
        SandboxFactory = sandboxFactory;
        LoggerFactory = loggerFactory;
    }

    /// <summary>
    /// Returns a failure <see cref="ToolResult"/> when <see cref="ModelId"/> is not configured,
    /// otherwise <c>null</c>. Callers use the null-coalescing pattern at method entry.
    /// </summary>
    protected ToolResult? EnsureModelConfigured(string toolName) =>
        string.IsNullOrWhiteSpace(ModelId)
            ? ToolResult.Fail($"No model ID configured for {toolName} sub-agents.")
            : null;

    /// <summary>
    /// Builds a <see cref="Internal.SubAgentRequest"/> pre-filled with the shared dependencies
    /// and parent sandbox options. Tools compose the remaining fields with a <c>with</c>
    /// expression at the call site.
    /// </summary>
    internal Internal.SubAgentRequest CreateBaseRequest(ToolContext context, string task) => new()
    {
        Task = task,
        ParentSandboxOptions = context.SandboxOptions,
        ModelProvider = ModelProvider,
        ModelId = ModelId,
        ToolRegistry = ToolRegistry,
        SandboxFactory = SandboxFactory,
        LoggerFactory = LoggerFactory,
    };
}
