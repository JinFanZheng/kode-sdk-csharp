using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Infrastructure.Providers;
using OpenAI;
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

    private sealed class CapturingHttpMessageHandler(List<string> bodies, string responseJson)
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
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
