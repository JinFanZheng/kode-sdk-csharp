using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kode.Agent.Boilerplate.Models;

public sealed record OpenAiChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("messages")]
    public required List<OpenAiChatMessage> Messages { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("user")]
    public string? User { get; init; }
}

public sealed record OpenAiChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public JsonElement Content { get; init; }

    public string GetTextContent()
    {
        if (Content.ValueKind == JsonValueKind.String)
        {
            return Content.GetString() ?? "";
        }

        if (Content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in Content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    parts.Add(item.GetString() ?? "");
                }
                else if (item.ValueKind == JsonValueKind.Object &&
                         item.TryGetProperty("text", out var text))
                {
                    parts.Add(text.GetString() ?? "");
                }
            }
            return string.Concat(parts);
        }

        if (Content.ValueKind == JsonValueKind.Object &&
            Content.TryGetProperty("text", out var textProp))
        {
            return textProp.GetString() ?? "";
        }

        return "";
    }
}

public sealed record OpenAiChatCompletionResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = "chat.completion";

    [JsonPropertyName("created")]
    public required long Created { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("choices")]
    public required List<OpenAiChatCompletionChoice> Choices { get; init; }

    [JsonPropertyName("usage")]
    public required OpenAiUsage Usage { get; init; }
}

public sealed record OpenAiChatCompletionChoice
{
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    [JsonPropertyName("message")]
    public required OpenAiChatCompletionMessage Message { get; init; }

    [JsonPropertyName("finish_reason")]
    public required string FinishReason { get; init; }
}

public sealed record OpenAiChatCompletionMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

public sealed record OpenAiUsage
{
    [JsonPropertyName("prompt_tokens")]
    public required int PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public required int CompletionTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public required int TotalTokens { get; init; }
}

public sealed record OpenAiStreamChunk
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = "chat.completion.chunk";

    [JsonPropertyName("created")]
    public required long Created { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("choices")]
    public required List<OpenAiStreamChoice> Choices { get; init; }
}

public sealed record OpenAiStreamChoice
{
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    [JsonPropertyName("delta")]
    public required OpenAiStreamDelta Delta { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

public sealed record OpenAiStreamDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    public List<OpenAiToolCall>? ToolCalls { get; init; }
}

public sealed record OpenAiToolCall
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required OpenAiToolFunction Function { get; init; }
}

public sealed record OpenAiToolFunction
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }
}

public sealed record OpenAiToolEvent
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("event")]
    public required string Event { get; init; } // "tool:start" | "tool:end" | "tool:error"

    [JsonPropertyName("tool_call_id")]
    public required string ToolCallId { get; init; }

    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("timestamp")]
    public required long Timestamp { get; init; }
}
