namespace Kode.Agent.Sdk.Core.Types;

using Kode.Agent.Sdk.Core.Agent;

/// <summary>
/// Dependencies required to create an agent.
/// </summary>
public record AgentDependencies
{
    /// <summary>
    /// The store for persistence.
    /// </summary>
    public required IAgentStore Store { get; init; }

    /// <summary>
    /// The sandbox factory for creating execution environments.
    /// </summary>
    public required ISandboxFactory SandboxFactory { get; init; }

    /// <summary>
    /// The tool registry.
    /// </summary>
    public required IToolRegistry ToolRegistry { get; init; }

    /// <summary>
    /// The model provider.
    /// </summary>
    public required IModelProvider ModelProvider { get; init; }

    /// <summary>
    /// Optional template registry (aligned with TypeScript `src/`).
    /// </summary>
    public Core.Templates.AgentTemplateRegistry? TemplateRegistry { get; init; }

    /// <summary>
    /// Optional logger factory.
    /// </summary>
    public Microsoft.Extensions.Logging.ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Optional tool-result compressor. When provided and AgentConfig.Context.ToolResultCompression
    /// is configured (any Enabled value), the injected compressor takes precedence over the
    /// built-in <see cref="LlmToolResultCompressor"/>. Hosts use this to plug in alternative
    /// strategies such as file-backed artifact offload without an LLM roundtrip.
    /// </summary>
    public IToolResultCompressor? ToolResultCompressor { get; init; }
}
