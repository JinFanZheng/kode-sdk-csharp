namespace Kode.Agent.Sdk.Core.Abstractions;

/// <summary>
/// LLM model provider interface for making AI model calls.
/// </summary>
public interface IModelProvider
{
    /// <summary>
    /// Gets the provider name (e.g., "anthropic", "openai").
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Makes a streaming completion request to the model.
    /// </summary>
    /// <param name="request">The model request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable of stream chunks.</returns>
    IAsyncEnumerable<StreamChunk> StreamAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes a non-streaming completion request to the model.
    /// </summary>
    /// <param name="request">The model request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete model response.</returns>
    Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that the provider is properly configured.
    /// </summary>
    Task<bool> ValidateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the capability profile for the given model.
    /// Returns null for unknown models, signaling the caller to use safe defaults.
    /// </summary>
    /// <param name="modelId">The model identifier (e.g., "deepseek-v4-pro").</param>
    /// <returns>Capabilities or null if the model is unknown.</returns>
    ModelCapabilities? GetModelCapabilities(string modelId);
}

/// <summary>
/// Request to the model.
/// </summary>
public record ModelRequest
{
    /// <summary>
    /// The model identifier (e.g., "claude-3-5-sonnet-20241022").
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// The conversation messages.
    /// </summary>
    public required IReadOnlyList<Message> Messages { get; init; }

    /// <summary>
    /// System prompt/instructions.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Available tools for the model to call.
    /// </summary>
    public IReadOnlyList<ToolSchema>? Tools { get; init; }

    /// <summary>
    /// Maximum tokens to generate.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Temperature for sampling (0.0 to 1.0).
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// Stop sequences.
    /// </summary>
    public IReadOnlyList<string>? StopSequences { get; init; }

    /// <summary>
    /// Whether to enable extended thinking (if supported).
    /// </summary>
    public bool EnableThinking { get; init; }

    /// <summary>
    /// Budget tokens for thinking (if enabled).
    /// </summary>
    public int? ThinkingBudget { get; init; }
}

/// <summary>
/// Complete response from the model.
/// </summary>
public record ModelResponse
{
    /// <summary>
    /// The response content blocks.
    /// </summary>
    public required IReadOnlyList<ContentBlock> Content { get; init; }

    /// <summary>
    /// The stop reason.
    /// </summary>
    public required ModelStopReason StopReason { get; init; }

    /// <summary>
    /// Token usage statistics.
    /// </summary>
    public required TokenUsage Usage { get; init; }

    /// <summary>
    /// The model used.
    /// </summary>
    public required string Model { get; init; }
}

/// <summary>
/// Streaming chunk from the model.
/// </summary>
public record StreamChunk
{
    /// <summary>
    /// The type of chunk.
    /// </summary>
    public required StreamChunkType Type { get; init; }

    /// <summary>
    /// Text delta (for text chunks).
    /// </summary>
    public string? TextDelta { get; init; }

    /// <summary>
    /// Thinking delta (for thinking chunks).
    /// </summary>
    public string? ThinkingDelta { get; init; }

    /// <summary>
    /// Thinking signature delta — emitted once per thinking block when the provider
    /// sends a <c>signature_delta</c> event (Anthropic / DeepSeek Anthropic-compatible).
    /// The agent layer captures and stores this alongside the accumulated thinking text
    /// so it can be passed back verbatim in future turns.
    /// </summary>
    public string? ThinkingSignature { get; init; }

    /// <summary>
    /// Tool use information (for tool_use chunks).
    /// </summary>
    public ToolUseChunk? ToolUse { get; init; }

    /// <summary>
    /// Stop reason (for message_stop chunks).
    /// </summary>
    public ModelStopReason? StopReason { get; init; }

    /// <summary>
    /// Usage (for message_stop chunks).
    /// </summary>
    public TokenUsage? Usage { get; init; }
}

/// <summary>
/// Type of stream chunk.
/// </summary>
public enum StreamChunkType
{
    /// <summary>Text content delta.</summary>
    TextDelta,
    /// <summary>Thinking content delta.</summary>
    ThinkingDelta,
    /// <summary>Tool use start.</summary>
    ToolUseStart,
    /// <summary>Tool use input delta.</summary>
    ToolUseInputDelta,
    /// <summary>Tool use complete.</summary>
    ToolUseComplete,
    /// <summary>Message stop.</summary>
    MessageStop
}

/// <summary>
/// Tool use chunk data.
/// </summary>
public record ToolUseChunk
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? InputDelta { get; init; }
    public object? Input { get; init; }
}

/// <summary>
/// Model stop reason.
/// </summary>
public enum ModelStopReason
{
    /// <summary>End of turn, model finished.</summary>
    EndTurn,
    /// <summary>Max tokens reached.</summary>
    MaxTokens,
    /// <summary>Stop sequence hit.</summary>
    StopSequence,
    /// <summary>Tool use requested.</summary>
    ToolUse,
    /// <summary>
    /// Model reports the request exceeded its context window (non-standard signal).
    /// Surfaced for providers that report overflow via a stop_reason string on HTTP 200
    /// rather than an error status (e.g. GLM's "model_context_window_exceeded").
    /// Triggers a force-compress-and-retry in the agent run loop.
    /// </summary>
    ContextOverflow
}

/// <summary>
/// Cache control type supported by a model.
/// </summary>
public enum CacheControlType
{
    /// <summary>No explicit cache control support.</summary>
    None,
    /// <summary>
    /// Ephemeral cache control — cache blocks are automatically managed by the provider
    /// with a TTL (DeepSeek V4 prefix cache, Anthropic prompt cache).
    /// </summary>
    Ephemeral,
    /// <summary>
    /// Prompt prefix cache — the provider caches the longest common prefix of the prompt
    /// automatically (OpenAI gpt-4.1 series, gpt-4o with automatic caching).
    /// </summary>
    PromptPrefix
}

/// <summary>
/// Model capability profile used by ContextManager for adaptive compression.
/// Providers declare known capabilities per model; unknown models return null
/// for graceful fallback to defaults.
/// </summary>
public record ModelCapabilities
{
    /// <summary>
    /// Context window size in tokens (e.g., 1_000_000 for DeepSeek V4, 200_000 for Claude).
    /// </summary>
    public required int ContextWindow { get; init; }

    /// <summary>
    /// Whether the model supports transparent prompt prefix caching.
    /// When true, replaying a message prefix should reuse cached tokens.
    /// </summary>
    public bool SupportsPrefixCache { get; init; }

    /// <summary>
    /// The cache control mechanism supported by this model.
    /// </summary>
    public CacheControlType CacheControlType { get; init; }

    /// <summary>
    /// Ratio of context window above which compression is triggered (default: 0.8).
    /// </summary>
    public double CompactionThresholdRatio { get; init; } = 0.8;

    /// <summary>
    /// Whether the model supports cache-aligned summary compression.
    /// When true, replaying original messages + appending a summary instruction
    /// preserves the prefix cache. Only valid for models with large context windows (≥ 500K)
    /// and transparent prefix caching.
    /// </summary>
    public bool SupportsCacheAlignedSummary { get; init; }
}

/// <summary>
/// Tool schema for model tool calling.
/// </summary>
public record ToolSchema
{
    /// <summary>
    /// The tool name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The tool description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// The input schema (JSON Schema format).
    /// </summary>
    public required object InputSchema { get; init; }
}
