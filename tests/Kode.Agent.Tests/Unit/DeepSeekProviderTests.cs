using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Infrastructure.Providers;
using Xunit;

namespace Kode.Agent.Tests.Unit;

/// <summary>
/// Tests for <see cref="DeepSeekProvider"/> covering:
/// - Non-streaming response parsing (CompleteAsync)
/// - Streaming SSE parsing (StreamAsync)
/// - Thinking mode (reasoning_content → ThinkingContent / ThinkingDelta)
/// - Tool use
/// - Message serialisation (request body)
/// - Assistant thinking history replay
/// </summary>
public sealed class DeepSeekProviderTests
{
    // ── Test response fixtures ────────────────────────────────────────────────

    private const string SimpleCompletionJson = """
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "deepseek-chat",
          "choices": [{
            "index": 0,
            "message": { "role": "assistant", "content": "Hello, world!" },
            "finish_reason": "stop"
          }],
          "usage": { "prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15 }
        }
        """;

    private const string ThinkingCompletionJson = """
        {
          "id": "chatcmpl-thinking",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "deepseek-chat",
          "choices": [{
            "index": 0,
            "message": {
              "role": "assistant",
              "reasoning_content": "Let me think about this carefully.",
              "content": "Here is my answer."
            },
            "finish_reason": "stop"
          }],
          "usage": { "prompt_tokens": 12, "completion_tokens": 8, "total_tokens": 20 }
        }
        """;

    private const string ToolUseCompletionJson = """
        {
          "id": "chatcmpl-tool",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "deepseek-chat",
          "choices": [{
            "index": 0,
            "message": {
              "role": "assistant",
              "content": "",
              "tool_calls": [{
                "id": "call_abc123",
                "type": "function",
                "function": {
                  "name": "get_weather",
                  "arguments": "{\\\"city\\\":\\\"London\\\"}"
                }
              }]
            },
            "finish_reason": "tool_calls"
          }],
          "usage": { "prompt_tokens": 15, "completion_tokens": 10, "total_tokens": 25 }
        }
        """;

    private const string ThinkingWithToolUseJson = """
        {
          "id": "chatcmpl-thinking-tool",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "deepseek-chat",
          "choices": [{
            "index": 0,
            "message": {
              "role": "assistant",
              "reasoning_content": "I need to look up the weather first.",
              "content": "",
              "tool_calls": [{
                "id": "call_xyz",
                "type": "function",
                "function": {
                  "name": "get_weather",
                  "arguments": "{\\\"city\\\":\\\"Paris\\\"}"
                }
              }]
            },
            "finish_reason": "tool_calls"
          }],
          "usage": { "prompt_tokens": 15, "completion_tokens": 12, "total_tokens": 27 }
        }
        """;

    // ── SSE fixtures ──────────────────────────────────────────────────────────

    private const string SimpleStreamSse = """
        data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}

        data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{"content":" world"},"finish_reason":null}]}

        data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}

        data: [DONE]

        """;

    private const string ThinkingStreamSse = """
        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{"reasoning_content":"Let me think"},"finish_reason":null}]}

        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{"reasoning_content":" about this."},"finish_reason":null}]}

        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{"content":"Answer"},"finish_reason":null}]}

        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":4,"total_tokens":14}}

        data: [DONE]

        """;

    // Built from properly JSON-escaped pieces to avoid C# raw-string escaping confusion.
    private static readonly string ToolUseStreamSse = BuildToolUseStreamSse();

    private static string BuildToolUseStreamSse()
    {
        var sb = new StringBuilder();
        sb.AppendLine("data: {\"id\":\"chatcmpl-tool-stream\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"deepseek-chat\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"\"}}]},\"finish_reason\":null}]}");
        sb.AppendLine();
        sb.AppendLine("data: {\"id\":\"chatcmpl-tool-stream\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"deepseek-chat\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"city\\\":\\\"NYC\\\"}\"}}]},\"finish_reason\":null}]}");
        sb.AppendLine();
        sb.AppendLine("data: {\"id\":\"chatcmpl-tool-stream\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"deepseek-chat\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":6,\"total_tokens\":16}}");
        sb.AppendLine();
        sb.Append("data: [DONE]");
        sb.AppendLine();
        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (DeepSeekProvider provider, List<string> capturedBodies) CreateProvider(
        string responseJson,
        string mediaType = "application/json")
    {
        var bodies = new List<string>();
        var handler = new CapturingHttpMessageHandler(bodies, responseJson, mediaType);
        var provider = new DeepSeekProvider(handler, new DeepSeekOptions { ApiKey = "test-key" });
        return (provider, bodies);
    }

    private static (DeepSeekProvider provider, List<string> capturedBodies) CreateStreamingProvider(
        string sseResponse)
    {
        return CreateProvider(sseResponse, "text/event-stream");
    }

    // ── CompleteAsync tests ───────────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_SimpleText_ReturnsTextContent()
    {
        var (provider, _) = CreateProvider(SimpleCompletionJson);

        var response = await provider.CompleteAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            Messages = [Message.User("Hello")],
            MaxTokens = 100
        });

        response.Content.Should().ContainSingle()
            .Which.Should().BeOfType<TextContent>()
            .Which.Text.Should().Be("Hello, world!");
        response.StopReason.Should().Be(ModelStopReason.EndTurn);
        response.Usage.InputTokens.Should().Be(10);
        response.Usage.OutputTokens.Should().Be(5);
    }

    [Fact]
    public async Task CompleteAsync_WithThinking_ProducesThinkingAndTextContent()
    {
        var (provider, _) = CreateProvider(ThinkingCompletionJson);

        var response = await provider.CompleteAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            Messages = [Message.User("Complex question")],
            MaxTokens = 100,
            EnableThinking = true
        });

        response.Content.Should().HaveCount(2);
        response.Content[0].Should().BeOfType<ThinkingContent>()
            .Which.Thinking.Should().Be("Let me think about this carefully.");
        response.Content[1].Should().BeOfType<TextContent>()
            .Which.Text.Should().Be("Here is my answer.");
    }

    [Fact]
    public async Task CompleteAsync_WithToolUse_ReturnsToolUseContent()
    {
        var (provider, _) = CreateProvider(ToolUseCompletionJson);

        var response = await provider.CompleteAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            Messages = [Message.User("What is the weather?")],
            MaxTokens = 100,
            Tools = [new ToolSchema
            {
                Name = "get_weather",
                Description = "Get weather",
                InputSchema = new { type = "object", properties = new { city = new { type = "string" } } }
            }]
        });

        response.Content.Should().ContainSingle()
            .Which.Should().BeOfType<ToolUseContent>();
        var toolUse = (ToolUseContent)response.Content[0];
        toolUse.Id.Should().Be("call_abc123");
        toolUse.Name.Should().Be("get_weather");
        response.StopReason.Should().Be(ModelStopReason.ToolUse);
    }

    [Fact]
    public async Task CompleteAsync_WithThinkingAndToolUse_ReturnsBoth()
    {
        var (provider, _) = CreateProvider(ThinkingWithToolUseJson);

        var response = await provider.CompleteAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            Messages = [Message.User("Weather in Paris?")],
            MaxTokens = 100,
            EnableThinking = true,
            Tools = [new ToolSchema
            {
                Name = "get_weather",
                Description = "Get weather",
                InputSchema = new { type = "object", properties = new { city = new { type = "string" } } }
            }]
        });

        response.Content.Should().HaveCount(2);
        response.Content[0].Should().BeOfType<ThinkingContent>()
            .Which.Thinking.Should().Be("I need to look up the weather first.");
        response.Content[1].Should().BeOfType<ToolUseContent>();
        response.StopReason.Should().Be(ModelStopReason.ToolUse);
    }

    // ── StreamAsync tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_SimpleText_EmitsTextDeltas()
    {
        var (provider, _) = CreateStreamingProvider(SimpleStreamSse);

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in provider.StreamAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            Messages = [Message.User("Hello")],
            MaxTokens = 100
        }))
        {
            chunks.Add(chunk);
        }

        var textDeltas = chunks.Where(c => c.Type == StreamChunkType.TextDelta)
            .Select(c => c.TextDelta).ToList();
        textDeltas.Should().ContainInOrder("Hello", " world");

        var stopChunk = chunks.Should().Contain(c => c.Type == StreamChunkType.MessageStop)
            .Which;
        stopChunk.StopReason.Should().Be(ModelStopReason.EndTurn);
        stopChunk.Usage.Should().NotBeNull();
        stopChunk.Usage!.InputTokens.Should().Be(10);
        stopChunk.Usage.OutputTokens.Should().Be(2);
    }

    [Fact]
    public async Task StreamAsync_WithThinking_EmitsThinkingAndTextDeltas()
    {
        var (provider, _) = CreateStreamingProvider(ThinkingStreamSse);

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in provider.StreamAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            Messages = [Message.User("Complex question")],
            MaxTokens = 100,
            EnableThinking = true
        }))
        {
            chunks.Add(chunk);
        }

        var thinkingDeltas = chunks.Where(c => c.Type == StreamChunkType.ThinkingDelta)
            .Select(c => c.ThinkingDelta).ToList();
        thinkingDeltas.Should().ContainInOrder("Let me think", " about this.");

        chunks.Should().Contain(c => c.Type == StreamChunkType.TextDelta
            && c.TextDelta == "Answer");
    }

    [Fact]
    public async Task StreamAsync_WithToolUse_EmitsToolUseChunks()
    {
        var (provider, _) = CreateStreamingProvider(ToolUseStreamSse);

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in provider.StreamAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            Messages = [Message.User("Weather in NYC?")],
            MaxTokens = 100,
            Tools = [new ToolSchema
            {
                Name = "get_weather",
                Description = "Get weather",
                InputSchema = new { type = "object", properties = new { city = new { type = "string" } } }
            }]
        }))
        {
            chunks.Add(chunk);
        }

        chunks.Should().Contain(c => c.Type == StreamChunkType.ToolUseStart
            && c.ToolUse!.Id == "call_1"
            && c.ToolUse.Name == "get_weather");

        var inputDeltas = chunks.Where(c => c.Type == StreamChunkType.ToolUseInputDelta)
            .Select(c => c.ToolUse!.InputDelta).ToList();
        inputDeltas.Should().ContainSingle()
            .Which.Should().Be("{\"city\":\"NYC\"}");

        chunks.Should().Contain(c => c.Type == StreamChunkType.ToolUseComplete);
        chunks.Should().Contain(c => c.Type == StreamChunkType.MessageStop
            && c.StopReason == ModelStopReason.ToolUse);
    }

    // ── Request body tests ────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_WithThinkingEnabled_InjectsThinkingField()
    {
        var (provider, bodies) = CreateProvider(ThinkingCompletionJson);

        await provider.CompleteAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            Messages = [Message.User("Hi")],
            MaxTokens = 100,
            EnableThinking = true
        });

        bodies.Should().ContainSingle();
        using var doc = JsonDocument.Parse(bodies[0]);
        doc.RootElement.TryGetProperty("thinking", out var thinking).Should().BeTrue();
        thinking.GetProperty("type").GetString().Should().Be("enabled");
        doc.RootElement.GetProperty("temperature").GetDouble().Should().Be(1.0);
    }

    [Fact]
    public async Task CompleteAsync_WithoutThinking_HasNoThinkingField()
    {
        var (provider, bodies) = CreateProvider(SimpleCompletionJson);

        await provider.CompleteAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            Messages = [Message.User("Hi")],
            MaxTokens = 100,
            EnableThinking = false,
            Temperature = 0.7
        });

        bodies.Should().ContainSingle();
        using var doc = JsonDocument.Parse(bodies[0]);
        doc.RootElement.TryGetProperty("thinking", out _).Should().BeFalse();
        doc.RootElement.GetProperty("temperature").GetDouble().Should().Be(0.7);
    }

    [Fact]
    public async Task CompleteAsync_SendsSystemPrompt()
    {
        var (provider, bodies) = CreateProvider(SimpleCompletionJson);

        await provider.CompleteAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            SystemPrompt = "You are a helpful assistant.",
            Messages = [Message.User("Hi")],
            MaxTokens = 100
        });

        bodies.Should().ContainSingle();
        using var doc = JsonDocument.Parse(bodies[0]);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        messages.Should().Contain(m =>
            m.GetProperty("role").GetString() == "system"
            && m.GetProperty("content").GetString() == "You are a helpful assistant.");
    }

    [Fact]
    public async Task CompleteAsync_SendsMemoryBlocksAsSystemMessages()
    {
        var (provider, bodies) = CreateProvider(SimpleCompletionJson);

        await provider.CompleteAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            SystemPrompt = "Main prompt.",
            Messages =
            [
                Message.System("<core-memory>Task: fix bugs</core-memory>"),
                Message.System("<context-summary>Previous summary</context-summary>"),
                Message.User("continue")
            ],
            MaxTokens = 100
        });

        bodies.Should().ContainSingle();
        var bodyText = bodies[0];
        bodyText.Should().Contain("core-memory");
        bodyText.Should().Contain("context-summary");
        bodyText.Should().Contain("Main prompt.");

        using var doc = JsonDocument.Parse(bodyText);
        var systemMessages = doc.RootElement.GetProperty("messages")
            .EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "system")
            .ToList();
        systemMessages.Should().HaveCount(3);
    }

    [Fact]
    public async Task CompleteAsync_AssistantThinkingHistory_ReplaysAsReasoningContent()
    {
        var (provider, bodies) = CreateProvider(SimpleCompletionJson);

        await provider.CompleteAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            EnableThinking = true,
            Messages =
            [
                Message.User("Complex question"),
                Message.Assistant(
                    new ThinkingContent { Thinking = "Let me reason step by step." },
                    new TextContent { Text = "The answer is 42." }
                ),
                Message.User("Thanks")
            ],
            MaxTokens = 100
        });

        bodies.Should().ContainSingle();
        using var doc = JsonDocument.Parse(bodies[0]);
        var assistantMsg = doc.RootElement.GetProperty("messages")
            .EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "assistant");

        assistantMsg.GetProperty("reasoning_content").GetString()
            .Should().Be("Let me reason step by step.");
        assistantMsg.GetProperty("content").GetString()
            .Should().Be("The answer is 42.");
    }

    [Fact]
    public async Task CompleteAsync_AssistantThinkingToolHistory_ReplaysAsReasoningContent()
    {
        var (provider, bodies) = CreateProvider(SimpleCompletionJson);

        await provider.CompleteAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            EnableThinking = true,
            Messages =
            [
                Message.User("Weather?"),
                Message.Assistant(
                    new ThinkingContent { Thinking = "I need to check the weather." },
                    new ToolUseContent
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        Input = new { city = "Shanghai" }
                    }),
                new Message
                {
                    Role = MessageRole.User,
                    Content =
                    [
                        new ToolResultContent
                        {
                            ToolUseId = "call_1",
                            Content = new { temperature = 22 }
                        }
                    ]
                }
            ],
            MaxTokens = 100
        });

        bodies.Should().ContainSingle();
        using var doc = JsonDocument.Parse(bodies[0]);
        var assistantMsg = doc.RootElement.GetProperty("messages")
            .EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "assistant");

        // reasoning_content should be set to the thinking text
        assistantMsg.GetProperty("reasoning_content").GetString()
            .Should().Be("I need to check the weather.");

        // content should be empty string since there's no visible text
        assistantMsg.GetProperty("content").GetString().Should().Be(string.Empty);

        // tool_calls should be present
        assistantMsg.TryGetProperty("tool_calls", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CompleteAsync_SendsStopSequences()
    {
        var (provider, bodies) = CreateProvider(SimpleCompletionJson);

        await provider.CompleteAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            Messages = [Message.User("Hi")],
            MaxTokens = 100,
            StopSequences = ["END", "STOP"]
        });

        bodies.Should().ContainSingle();
        using var doc = JsonDocument.Parse(bodies[0]);
        var stops = doc.RootElement.GetProperty("stop").EnumerateArray()
            .Select(s => s.GetString()).ToList();
        stops.Should().ContainInOrder("END", "STOP");
    }

    // ── Provider identity ─────────────────────────────────────────────────────

    [Fact]
    public void ProviderName_IsDeepSeek()
    {
        var (provider, _) = CreateProvider(SimpleCompletionJson);
        provider.ProviderName.Should().Be("deepseek");
    }

    // =========================================================================
    // CapturingHttpMessageHandler
    // =========================================================================

    private sealed class CapturingHttpMessageHandler(
        List<string> bodies,
        string responseJson,
        string mediaType = "application/json")
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, mediaType)
            };
        }
    }
}
