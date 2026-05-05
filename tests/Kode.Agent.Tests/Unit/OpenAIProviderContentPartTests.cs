using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using System.ClientModel;
using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Infrastructure.Providers;
using OpenAI;
using OpenAI.Chat;
using Xunit;

namespace Kode.Agent.Tests.Unit;

/// <summary>
/// Verifies that <see cref="OpenAIProvider"/> serialises <see cref="VideoContent"/>
/// and <see cref="FileContent"/> into the correct OpenAI-compatible JSON content-part
/// shapes (<c>video_url</c> / <c>file_url</c>).
///
/// Two complementary strategies:
///  1. <see cref="MediaRewriteHttpHandler.TransformMediaMarkers"/> is tested directly
///     (pure unit, no HTTP at all).
///  2. The full provider pipeline is exercised with a mock HTTP transport.
///     The handler chain is: MediaRewriteHttpHandler → CapturingHttpMessageHandler,
///     so the captured body reflects the already-transformed JSON.
/// </summary>
public sealed class OpenAIProviderContentPartTests
{
    // Minimal valid OpenAI chat-completion response that satisfies the SDK parser.
    private const string FakeCompletionJson = """
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "glm-4v-plus",
          "choices": [{
            "index": 0,
            "message": { "role": "assistant", "content": "ok" },
            "finish_reason": "stop"
          }],
          "usage": { "prompt_tokens": 10, "completion_tokens": 2, "total_tokens": 12 }
        }
        """;

    private const string FakeThinkingStreamSse = """
        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{"reasoning_content":"first reason"},"finish_reason":null}]}

        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{"content":"final answer"},"finish_reason":null}]}

        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}

        data: [DONE]

        """;

    private const string FakeThinkingStreamSseMultiFragment = """
        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{"reasoning_content":"好的"},"finish_reason":null}]}

        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{"reasoning_content":"，"},"finish_reason":null}]}

        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{"reasoning_content":"用户"},"finish_reason":null}]}

        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{"reasoning_content":"想"},"finish_reason":null}]}

        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{"reasoning_content":"测试"},"finish_reason":null}]}

        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{"content":"hi"},"finish_reason":null}]}

        data: {"id":"chatcmpl-thinking","object":"chat.completion.chunk","created":1700000000,"model":"deepseek-chat","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}

        data: [DONE]

        """;

    private static (OpenAIProvider provider, List<string> capturedBodies) CreateProviderWithCapture()
    {
        var bodies = new List<string>();
        // Chain: MediaRewriteHttpHandler rewrites markers, then CapturingHttpMessageHandler
        // captures the already-transformed body and returns a fake response.
        var captureHandler = new CapturingHttpMessageHandler(bodies, FakeCompletionJson);
        var rewriteHandler = new MediaRewriteHttpHandler(captureHandler);

        var clientOptions = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(rewriteHandler)),
            Endpoint = new Uri("https://mock.local/api/paas/v4/")
        };

        // Internal constructor — does not inject a second MediaRewriteHttpHandler
        var provider = new OpenAIProvider(new OpenAIOptions { ApiKey = "test-key" }, clientOptions);
        return (provider, bodies);
    }

    private static OpenAIProvider CreateProviderWithStreamingResponse(string sseResponse)
    {
        var captureHandler = new CapturingHttpMessageHandler([], sseResponse, "text/event-stream");
        var rewriteHandler = new MediaRewriteHttpHandler(captureHandler);

        var clientOptions = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(rewriteHandler)),
            Endpoint = new Uri("https://mock.local/api/paas/v4/")
        };

        return new OpenAIProvider(new OpenAIOptions { ApiKey = "test-key" }, clientOptions);
    }

    private static ChatClient CreateChatClientWithStreamingResponse(string sseResponse)
    {
        var captureHandler = new CapturingHttpMessageHandler([], sseResponse, "text/event-stream");
        var rewriteHandler = new MediaRewriteHttpHandler(captureHandler);

        var clientOptions = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(rewriteHandler)),
            Endpoint = new Uri("https://mock.local/api/paas/v4/")
        };

        var client = new OpenAIClient(new ApiKeyCredential("test-key"), clientOptions);
        return client.GetChatClient("deepseek-chat");
    }

    // =========================================================================
    // 1. Direct unit tests for MediaRewriteHttpHandler.TransformMediaMarkers
    // =========================================================================

    [Fact]
    public void TransformMediaMarkers_NoMarkers_ReturnsSameReference()
    {
        const string json = """{"messages":[{"role":"user","content":"hello"}]}""";
        var result = MediaRewriteHttpHandler.TransformMediaMarkers(json);
        result.Should().BeSameAs(json);
    }

    [Fact]
    public void TransformMediaMarkers_VideoMarker_ReplacesWithVideoUrlShape()
    {
        var videoUrl = "https://cdn.bigmodel.cn/agent-demos/lark/113123.mov";
        var input = BuildUserTextPartJson(MediaRewriteHttpHandler.VideoMarker + videoUrl);

        var output = MediaRewriteHttpHandler.TransformMediaMarkers(input);

        using var doc = JsonDocument.Parse(output);
        var part = GetFirstUserContentPart(doc);
        part.GetProperty("type").GetString().Should().Be("video_url");
        part.GetProperty("video_url").GetProperty("url").GetString().Should().Be(videoUrl);
    }

    [Fact]
    public void TransformMediaMarkers_FileMarker_ReplacesWithFileUrlShape()
    {
        var fileUrl = "https://cdn.bigmodel.cn/static/demo/demo2.txt";
        var input = BuildUserTextPartJson(MediaRewriteHttpHandler.FileMarker + fileUrl);

        var output = MediaRewriteHttpHandler.TransformMediaMarkers(input);

        using var doc = JsonDocument.Parse(output);
        var part = GetFirstUserContentPart(doc);
        part.GetProperty("type").GetString().Should().Be("file_url");
        part.GetProperty("file_url").GetProperty("url").GetString().Should().Be(fileUrl);
    }

    [Fact]
    public void TransformThinking_WhenEnabled_InjectsThinkingAndRemovesWrongReasoningContentField()
    {
        const string json = """{"messages":[{"role":"user","content":"hello"}],"reasoning_content":"high"}""";
        MediaRewriteHttpHandler.ThinkingEnabled.Value = true;
        try
        {
            var output = MediaRewriteHttpHandler.TransformThinking(json);
            using var doc = JsonDocument.Parse(output);
            doc.RootElement.GetProperty("thinking").GetProperty("type").GetString().Should().Be("enabled");
            doc.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("high");
            doc.RootElement.TryGetProperty("reasoning_content", out _).Should().BeFalse();
        }
        finally
        {
            MediaRewriteHttpHandler.ThinkingEnabled.Value = false;
        }
    }

    [Fact]
    public void TransformReasoningSseLine_RewritesReasoningContentIntoMarkerizedContent()
    {
        const string sseLine = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"deep thought\",\"content\":\" visible\"}}]}";
        MediaRewriteHttpHandler.ThinkingEnabled.Value = true;
        try
        {
            var output = MediaRewriteHttpHandler.TransformReasoningSseLine(sseLine);
            output.Should().Contain(OpenAIProvider.ThinkingMarkerStart);
            output.Should().Contain(OpenAIProvider.ThinkingMarkerEnd);
            output.Should().Contain("deep thought");
            output.Should().Contain(" visible");
        }
        finally
        {
            MediaRewriteHttpHandler.ThinkingEnabled.Value = false;
        }
    }

    // =========================================================================
    // 2. Full pipeline tests (mock HTTP transport)
    // =========================================================================

    [Fact]
    public async Task CompleteAsync_WithVideoContent_SendsVideoUrlShape()
    {
        var (provider, bodies) = CreateProviderWithCapture();

        await provider.CompleteAsync(new ModelRequest
        {
            Model = "glm-4v-plus",
            Messages =
            [
                new Message
                {
                    Role = MessageRole.User,
                    Content =
                    [
                        new TextContent { Text = "What is in this video?" },
                        VideoContent.FromUrl("https://cdn.bigmodel.cn/agent-demos/lark/113123.mov")
                    ]
                }
            ],
            MaxTokens = 10
        });

        bodies.Should().ContainSingle();
        var body = bodies[0];
        body.Should().Contain("video_url");
        body.Should().Contain("https://cdn.bigmodel.cn/agent-demos/lark/113123.mov");
        body.Should().NotContain("image_url");
    }

    [Fact]
    public async Task CompleteAsync_WithFileContent_SendsFileUrlShape()
    {
        var (provider, bodies) = CreateProviderWithCapture();

        await provider.CompleteAsync(new ModelRequest
        {
            Model = "glm-4v-plus",
            Messages =
            [
                new Message
                {
                    Role = MessageRole.User,
                    Content =
                    [
                        new TextContent { Text = "Summarise this document." },
                        FileContent.FromUrl("https://cdn.bigmodel.cn/static/demo/demo2.txt")
                    ]
                }
            ],
            MaxTokens = 10
        });

        bodies.Should().ContainSingle();
        var body = bodies[0];
        body.Should().Contain("file_url");
        body.Should().Contain("https://cdn.bigmodel.cn/static/demo/demo2.txt");
        body.Should().NotContain("image_url");
    }

    // Note: mixing video + file in one request is not supported by the GLM API at runtime.
    // This test only verifies that the SDK-level JSON serialisation is correct for each part type.
    [Fact]
    public async Task CompleteAsync_WithMixedContent_SendsAllParts()
    {
        var (provider, bodies) = CreateProviderWithCapture();

        await provider.CompleteAsync(new ModelRequest
        {
            Model = "glm-4v-plus",
            Messages =
            [
                new Message
                {
                    Role = MessageRole.User,
                    Content =
                    [
                        new TextContent { Text = "Describe these:" },
                        VideoContent.FromUrl("https://example.com/clip.mp4"),
                        FileContent.FromUrl("https://example.com/doc.pdf")
                    ]
                }
            ],
            MaxTokens = 10
        });

        bodies.Should().ContainSingle();
        using var doc = JsonDocument.Parse(bodies[0]);
        var messages = doc.RootElement.GetProperty("messages");
        var userMsg = messages.EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "user");
        var parts = userMsg.GetProperty("content").EnumerateArray().ToList();

        parts.Should().HaveCount(3);
        parts[0].GetProperty("type").GetString().Should().Be("text");
        parts[1].GetProperty("type").GetString().Should().Be("video_url");
        parts[1].GetProperty("video_url").GetProperty("url").GetString()
            .Should().Be("https://example.com/clip.mp4");
        parts[2].GetProperty("type").GetString().Should().Be("file_url");
        parts[2].GetProperty("file_url").GetProperty("url").GetString()
            .Should().Be("https://example.com/doc.pdf");
    }

    [Fact]
    public async Task CompleteAsync_TextOnlyMessage_ContentIsString()
    {
        var (provider, bodies) = CreateProviderWithCapture();

        await provider.CompleteAsync(new ModelRequest
        {
            Model = "glm-4v-plus",
            Messages = [Message.User("plain text")],
            MaxTokens = 10
        });

        using var doc = JsonDocument.Parse(bodies[0]);
        var messages = doc.RootElement.GetProperty("messages");
        var userMsg = messages.EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "user");

        // Text-only path: content must be a JSON string, not an array
        userMsg.GetProperty("content").ValueKind.Should().Be(JsonValueKind.String);
    }

    // Bug #1 regression: system-role messages (core-memory / context-summary
    // emitted by ContextManager) were silently dropped by the old filter, which
    // defeated the three-layer compression architecture and caused the model to
    // re-overflow on compressed sessions. They must reach the API as additional
    // system-role messages at the head of the array.
    [Fact]
    public async Task CompleteAsync_WithSystemRoleMemoryMessages_PreservesThemAsSystemMessages()
    {
        var (provider, bodies) = CreateProviderWithCapture();

        await provider.CompleteAsync(new ModelRequest
        {
            Model = "glm-4v-plus",
            SystemPrompt = "You are helpful.",
            Messages =
            [
                Message.System("<core-memory updated=\"2026-04-19T10:00:00\">\n## Current Task\nfix bugs\n</core-memory>"),
                Message.System("<context-summary timestamp=\"2026-04-19T10:00:00\" window=\"w1\">\nSummary text\n</context-summary>"),
                Message.User("continue"),
            ],
            MaxTokens = 10
        });

        bodies.Should().ContainSingle();
        using var doc = JsonDocument.Parse(bodies[0]);
        var systemMessages = doc.RootElement.GetProperty("messages")
            .EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "system")
            .ToList();

        systemMessages.Should().HaveCount(3, "main prompt + core-memory + summary");
        var bodyText = bodies[0];
        bodyText.Should().Contain("core-memory");
        bodyText.Should().Contain("context-summary");
        bodyText.Should().Contain("You are helpful.");
    }

        [Fact]
        public async Task CompleteAsync_WithReasoningContentResponse_ProducesThinkingContent()
        {
                const string reasoningResponseJson = """
                        {
                            "id": "chatcmpl-thinking",
                            "object": "chat.completion",
                            "created": 1700000000,
                            "model": "deepseek-chat",
                            "choices": [{
                                "index": 0,
                                "message": {
                                    "role": "assistant",
                                    "reasoning_content": "first reason",
                                    "content": "final answer"
                                },
                                "finish_reason": "stop"
                            }],
                            "usage": { "prompt_tokens": 10, "completion_tokens": 2, "total_tokens": 12 }
                        }
                        """;

                var bodies = new List<string>();
                var captureHandler = new CapturingHttpMessageHandler(bodies, reasoningResponseJson);
                var rewriteHandler = new MediaRewriteHttpHandler(captureHandler);
                var clientOptions = new OpenAIClientOptions
                {
                        Transport = new HttpClientPipelineTransport(new HttpClient(rewriteHandler)),
                        Endpoint = new Uri("https://mock.local/api/paas/v4/")
                };
                var provider = new OpenAIProvider(new OpenAIOptions { ApiKey = "test-key" }, clientOptions);

                var response = await provider.CompleteAsync(new ModelRequest
                {
                        Model = "deepseek-chat",
                        EnableThinking = true,
                        Messages = [Message.User("plain text")],
                        MaxTokens = 10
                });

                response.Content.OfType<ThinkingContent>().Should().ContainSingle();
                response.Content.OfType<ThinkingContent>().Single().Thinking.Should().Be("first reason");
                response.Content.OfType<TextContent>().Should().ContainSingle();
                response.Content.OfType<TextContent>().Single().Text.Should().Be("final answer");
        }

            [Fact]
            public async Task StreamAsync_WithReasoningContentSse_EmitsThinkingDelta()
            {
                var provider = CreateProviderWithStreamingResponse(FakeThinkingStreamSse);

                var chunks = new List<StreamChunk>();
                await foreach (var chunk in provider.StreamAsync(new ModelRequest
                {
                    Model = "deepseek-chat",
                    EnableThinking = true,
                    Messages = [Message.User("plain text")],
                    MaxTokens = 10
                }))
                {
                    chunks.Add(chunk);
                }

                chunks.Should().Contain(c => c.Type == StreamChunkType.ThinkingDelta && c.ThinkingDelta == "first reason");
                chunks.Should().Contain(c => c.Type == StreamChunkType.TextDelta && c.TextDelta == "final answer");
            }

            [Fact]
            public async Task StreamAsync_WithMultiFragmentReasoningContentSse_EmitsAllThinkingDeltas()
            {
                var provider = CreateProviderWithStreamingResponse(FakeThinkingStreamSseMultiFragment);

                var chunks = new List<StreamChunk>();
                await foreach (var chunk in provider.StreamAsync(new ModelRequest
                {
                    Model = "deepseek-chat",
                    EnableThinking = true,
                    Messages = [Message.User("plain text")],
                    MaxTokens = 10
                }))
                {
                    chunks.Add(chunk);
                }

                chunks.Where(c => c.Type == StreamChunkType.ThinkingDelta)
                    .Select(c => c.ThinkingDelta)
                    .Should().ContainInOrder("好的", "，", "用户", "想", "测试");
                chunks.Should().Contain(c => c.Type == StreamChunkType.TextDelta && c.TextDelta == "hi");
            }

            [Fact]
            public async Task OpenAiSdkStreaming_WithMultiFragmentReasoningContentSse_ExposesAllContentUpdates()
            {
                var chatClient = CreateChatClientWithStreamingResponse(FakeThinkingStreamSseMultiFragment);
                MediaRewriteHttpHandler.ThinkingEnabled.Value = true;
                try
                {
                    var updates = new List<string>();
                    await foreach (var update in chatClient.CompleteChatStreamingAsync(
                                       [new UserChatMessage("plain text")],
                                       new ChatCompletionOptions()))
                    {
                        updates.AddRange(update.ContentUpdate.Where(part => !string.IsNullOrEmpty(part.Text)).Select(part => part.Text));
                    }

                    updates.Should().ContainInOrder(
                        OpenAIProvider.ThinkingMarkerStart + "好的" + OpenAIProvider.ThinkingMarkerEnd,
                        OpenAIProvider.ThinkingMarkerStart + "，" + OpenAIProvider.ThinkingMarkerEnd,
                        OpenAIProvider.ThinkingMarkerStart + "用户" + OpenAIProvider.ThinkingMarkerEnd,
                        OpenAIProvider.ThinkingMarkerStart + "想" + OpenAIProvider.ThinkingMarkerEnd,
                        OpenAIProvider.ThinkingMarkerStart + "测试" + OpenAIProvider.ThinkingMarkerEnd,
                        "hi");
                }
                finally
                {
                    MediaRewriteHttpHandler.ThinkingEnabled.Value = false;
                }
            }

            [Fact]
            public async Task CompleteAsync_WithDeepSeekAssistantThinkingToolHistory_NowUsesReasoningContentField()
            {
                var (provider, bodies) = CreateProviderWithCapture();

                await provider.CompleteAsync(new ModelRequest
                {
                    Model = "deepseek-chat",
                    EnableThinking = true,
                    Messages =
                    [
                        Message.User("plain text"),
                        Message.Assistant(
                            new ThinkingContent { Thinking = "first reason" },
                            new ToolUseContent
                            {
                                Id = "call_1",
                                Name = "lookup_weather",
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
                                    Content = new { ok = true }
                                }
                            ]
                        }
                    ],
                    MaxTokens = 10
                });

                bodies.Should().ContainSingle();
                using var doc = JsonDocument.Parse(bodies[0]);
                var assistantMessage = doc.RootElement.GetProperty("messages")
                    .EnumerateArray()
                    .First(m => m.GetProperty("role").GetString() == "assistant");

                // After DeepSeekProvider extraction, OpenAIProvider treats DeepSeek models
                // the same as other models: thinking goes to reasoning_content field.
                assistantMessage.GetProperty("reasoning_content").GetString().Should().Be("first reason");
                assistantMessage.GetProperty("content").ValueKind.Should().Be(JsonValueKind.String);
                assistantMessage.GetProperty("content").GetString().Should().Be(string.Empty);
                bodies[0].Should().NotContain(OpenAIProvider.ThinkingMarkerStart);
                bodies[0].Should().NotContain(OpenAIProvider.ThinkingMarkerEnd);
            }

            [Fact]
            public async Task CompleteAsync_WithNonDeepSeekAssistantThinkingToolHistory_KeepsReasoningContentField()
            {
                var (provider, bodies) = CreateProviderWithCapture();

                await provider.CompleteAsync(new ModelRequest
                {
                    Model = "gpt-4o-mini",
                    EnableThinking = true,
                    Messages =
                    [
                        Message.User("plain text"),
                        Message.Assistant(
                            new ThinkingContent { Thinking = "first reason" },
                            new ToolUseContent
                            {
                                Id = "call_1",
                                Name = "lookup_weather",
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
                                    Content = new { ok = true }
                                }
                            ]
                        }
                    ],
                    MaxTokens = 10
                });

                bodies.Should().ContainSingle();
                using var doc = JsonDocument.Parse(bodies[0]);
                var assistantMessage = doc.RootElement.GetProperty("messages")
                    .EnumerateArray()
                    .First(m => m.GetProperty("role").GetString() == "assistant");

                assistantMessage.GetProperty("reasoning_content").GetString().Should().Be("first reason");
                assistantMessage.GetProperty("content").ValueKind.Should().Be(JsonValueKind.String);
                assistantMessage.GetProperty("content").GetString().Should().Be(string.Empty);
                bodies[0].Should().NotContain(OpenAIProvider.ThinkingMarkerStart);
                bodies[0].Should().NotContain(OpenAIProvider.ThinkingMarkerEnd);
            }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static string BuildUserTextPartJson(string textValue) =>
        JsonSerializer.Serialize(new
        {
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new[] { new { type = "text", text = textValue } }
                }
            }
        });

    private static JsonElement GetFirstUserContentPart(JsonDocument doc) =>
        doc.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .First(m => m.GetProperty("role").GetString() == "user")
            .GetProperty("content")
            .EnumerateArray()
            .First();

    private sealed class CapturingHttpMessageHandler(List<string> bodies, string responseJson, string mediaType = "application/json")
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
