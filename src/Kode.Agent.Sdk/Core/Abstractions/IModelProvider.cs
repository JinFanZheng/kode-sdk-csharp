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
