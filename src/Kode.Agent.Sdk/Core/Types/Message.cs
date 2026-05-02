using System.Text.Json.Serialization;

namespace Kode.Agent.Sdk.Core.Types;

/// <summary>
/// Message in a conversation.
/// </summary>
public record Message
{
    /// <summary>
    /// The role of the message sender.
    /// </summary>
    public required MessageRole Role { get; init; }

    /// <summary>
    /// The content blocks of the message.
    /// </summary>
    public required IReadOnlyList<ContentBlock> Content { get; init; }

    /// <summary>
    /// Creates a user message with text content.
    /// </summary>
    public static Message User(string text) => new()
    {
        Role = MessageRole.User,
        Content = [new TextContent { Text = text }]
    };

    /// <summary>
    /// Creates an assistant message with text content.
    /// </summary>
    public static Message Assistant(string text) => new()
    {
        Role = MessageRole.Assistant,
        Content = [new TextContent { Text = text }]
    };

    /// <summary>
    /// Creates an assistant message with content blocks.
    /// </summary>
    public static Message Assistant(params ContentBlock[] content) => new()
    {
        Role = MessageRole.Assistant,
        Content = content
    };

    /// <summary>
    /// Creates a system message.
    /// </summary>
    public static Message System(string text) => new()
    {
        Role = MessageRole.System,
        Content = [new TextContent { Text = text }]
    };
}

/// <summary>
/// Role of a message sender.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageRole
{
    /// <summary>User message.</summary>
    User,
    /// <summary>Assistant (AI) message.</summary>
    Assistant,
    /// <summary>System message.</summary>
    System
}

/// <summary>
/// Base class for content blocks.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ToolUseContent), "tool_use")]
[JsonDerivedType(typeof(ToolResultContent), "tool_result")]
[JsonDerivedType(typeof(ThinkingContent), "thinking")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(VideoContent), "video")]
[JsonDerivedType(typeof(FileContent), "file")]
public abstract record ContentBlock
{
    /// <summary>
    /// The type of content block.
    /// </summary>
    [JsonIgnore]
    public abstract string Type { get; }
}

/// <summary>
/// Text content block.
/// </summary>
public record TextContent : ContentBlock
{
    /// <inheritdoc />
    public override string Type => "text";

    /// <summary>
    /// The text content.
    /// </summary>
    public required string Text { get; init; }
}

/// <summary>
/// Thinking/reasoning content block.
/// </summary>
public record ThinkingContent : ContentBlock
{
    /// <inheritdoc />
    public override string Type => "thinking";

    /// <summary>
    /// The thinking content.
    /// </summary>
    public required string Thinking { get; init; }

    /// <summary>
    /// Cryptographic signature returned by the provider (Anthropic / DeepSeek Anthropic-compatible).
    /// Must be passed back verbatim in subsequent turns when tool calls were made.
    /// </summary>
    public string? Signature { get; init; }
}

/// <summary>
/// Tool use content block (assistant requesting tool call).
/// </summary>
public record ToolUseContent : ContentBlock
{
    /// <inheritdoc />
    public override string Type => "tool_use";

    /// <summary>
    /// Unique ID for this tool call.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Name of the tool to call.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Input arguments for the tool.
    /// </summary>
    public required object Input { get; init; }
}

/// <summary>
/// Image content block (user-provided image for vision models).
/// Supports both URL-referenced images and base64-encoded data.
/// </summary>
public record ImageContent : ContentBlock
{
    /// <inheritdoc />
    public override string Type => "image";

    /// <summary>
    /// The media type of the image (e.g. "image/png", "image/jpeg").
    /// Required when <see cref="Data"/> is provided; ignored when <see cref="Url"/> is set.
    /// </summary>
    public string? MediaType { get; init; }

    /// <summary>
    /// Base64-encoded image data. Mutually exclusive with <see cref="Url"/>.
    /// </summary>
    public string? Data { get; init; }

    /// <summary>
    /// URL of the image. Mutually exclusive with <see cref="Data"/>.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Creates an image content block from base64 data.
    /// </summary>
    public static ImageContent FromBase64(string mediaType, string base64Data) => new()
    {
        MediaType = mediaType,
        Data = base64Data
    };

    /// <summary>
    /// Creates an image content block from a URL.
    /// </summary>
    public static ImageContent FromUrl(string url) => new()
    {
        Url = url
    };
}

/// <summary>
/// Video content block (user-provided video URL).
/// </summary>
public record VideoContent : ContentBlock
{
    /// <inheritdoc />
    public override string Type => "video";

    /// <summary>
    /// URL of the video.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Creates a video content block from a URL.
    /// </summary>
    public static VideoContent FromUrl(string url) => new() { Url = url };
}

/// <summary>
/// File content block (user-provided file URL).
/// </summary>
public record FileContent : ContentBlock
{
    /// <inheritdoc />
    public override string Type => "file";

    /// <summary>
    /// URL of the file.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Creates a file content block from a URL.
    /// </summary>
    public static FileContent FromUrl(string url) => new() { Url = url };
}

/// <summary>
/// Tool result content block (result of tool execution).
/// </summary>
public record ToolResultContent : ContentBlock
{
    /// <inheritdoc />
    public override string Type => "tool_result";

    /// <summary>
    /// The ID of the tool call this result is for.
    /// </summary>
    public required string ToolUseId { get; init; }

    /// <summary>
    /// The result content.
    /// </summary>
    public required object Content { get; init; }

    /// <summary>
    /// Whether this is an error result.
    /// </summary>
    public bool IsError { get; init; }
}
