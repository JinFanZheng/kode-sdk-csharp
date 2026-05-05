using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Types;
using Moq;
using Xunit;

namespace Kode.Agent.Tests.Unit.Context;

public sealed class LlmContextSummarizerTests
{
    private static ModelResponse ResponseWith(string text) => new()
    {
        Content = [new TextContent { Text = text }],
        StopReason = ModelStopReason.EndTurn,
        Usage = new TokenUsage { InputTokens = 0, OutputTokens = 0 },
        Model = "fake"
    };

    [Fact]
    public async Task Summarize_SendsSystemPromptWithKeyDirectives()
    {
        ModelRequest? captured = null;
        var provider = new Mock<IModelProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ModelRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(ResponseWith("<summary>ok</summary><core-memory>ctx</core-memory>"));

        var summarizer = new LlmContextSummarizer(provider.Object, primaryModel: "haiku");

        var removed = new List<Message>
        {
            Message.User("do the thing"),
            Message.Assistant("doing it"),
        };

        var result = await summarizer.SummarizeAsync(removed, new ContextManagerOptions());

        result.Summary.Should().Be("ok");
        result.CoreMemoryUpdate.Should().Be("ctx");

        captured.Should().NotBeNull();
        captured.MaxTokens.Should().Be(1200);
        var sys = captured.SystemPrompt ?? "";
        sys.Should().Contain("<summary>", because: "response XML schema must be specified");
        sys.Should().Contain("<core-memory>", because: "response XML schema must include core-memory");
        sys.Should().MatchRegex(@"\b500\b", because: "summary word budget must be explicit");
    }

    [Fact]
    public async Task Summarize_UsesCustomPromptWhenProvided()
    {
        ModelRequest? captured = null;
        var provider = new Mock<IModelProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ModelRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(ResponseWith("<summary>x</summary>"));

        var summarizer = new LlmContextSummarizer(provider.Object);
        await summarizer.SummarizeAsync(
            [Message.User("hi")],
            new ContextManagerOptions { CompressionPrompt = "CUSTOM_PROMPT_TOKEN" });

        captured!.SystemPrompt.Should().Be("CUSTOM_PROMPT_TOKEN");
    }

    [Fact]
    public async Task Summarize_FallsBackToStaticOnEmptyLlmText()
    {
        var provider = new Mock<IModelProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseWith(""));

        var summarizer = new LlmContextSummarizer(provider.Object);

        var result = await summarizer.SummarizeAsync(
            [Message.User("hello")],
            new ContextManagerOptions());

        result.Summary.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Summarize_FallsBackOnLlmException()
    {
        var provider = new Mock<IModelProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var summarizer = new LlmContextSummarizer(provider.Object);
        var result = await summarizer.SummarizeAsync(
            [Message.User("x")],
            new ContextManagerOptions());

        result.Summary.Should().NotBeNullOrWhiteSpace();
    }

    // Regression: a force-compress triggered by model_empty_response often removes
    // only tool_use/tool_result pairs (assistant + tool_result-only user messages).
    // Previously BuildCompressionContext ignored ToolResultContent entirely, so the
    // LLM received an empty body and returned "No conversation history was provided".
    [Fact]
    public async Task Summarize_IncludesToolResultsInCompressionBody()
    {
        ModelRequest? captured = null;
        var provider = new Mock<IModelProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ModelRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(ResponseWith("<summary>x</summary>"));

        var summarizer = new LlmContextSummarizer(provider.Object);
        var removed = new List<Message>
        {
            new() { Role = MessageRole.Assistant, Content = [new ToolUseContent { Id = "t1", Name = "bash_run", Input = new { command = "git log" } }] },
            new() { Role = MessageRole.User, Content = [new ToolResultContent { ToolUseId = "t1", Content = new { exitCode = 0, stdout = "abc123 commit message" }, IsError = false }] },
            new() { Role = MessageRole.Assistant, Content = [new ToolUseContent { Id = "t2", Name = "fs_read", Input = new { path = "/foo" } }] },
            new() { Role = MessageRole.User, Content = [new ToolResultContent { ToolUseId = "t2", Content = "plain string payload", IsError = false }] },
        };

        await summarizer.SummarizeAsync(removed, new ContextManagerOptions());

        var body = captured!.Messages[0].Content.OfType<TextContent>().First().Text;
        body.Should().Contain("[ToolResult:t1]:");
        body.Should().Contain("abc123");
        body.Should().Contain("[ToolResult:t2]:");
        body.Should().Contain("plain string payload");
        body.Should().Contain("Total 2 tool calls, 2 tool results");
    }

    [Fact]
    public async Task Summarize_MarksToolErrorsDistinctly()
    {
        ModelRequest? captured = null;
        var provider = new Mock<IModelProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ModelRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(ResponseWith("<summary>x</summary>"));

        var summarizer = new LlmContextSummarizer(provider.Object);
        var removed = new List<Message>
        {
            new() { Role = MessageRole.User, Content = [new ToolResultContent { ToolUseId = "err1", Content = "permission denied", IsError = true }] },
        };

        await summarizer.SummarizeAsync(removed, new ContextManagerOptions());

        var body = captured!.Messages[0].Content.OfType<TextContent>().First().Text;
        body.Should().Contain("[ToolError:err1]:");
        body.Should().Contain("permission denied");
    }

    // ── Cache-aligned summary path ───────────────────────────────────────────

    [Fact]
    public async Task Summarize_UsesCacheAlignedPath_WhenCapabilitiesAllow()
    {
        ModelRequest? captured = null;
        var provider = new Mock<IModelProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ModelRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(ResponseWith("<summary>aligned-ok</summary>"));

        // Setup capabilities for V4 model
        provider.Setup(p => p.GetModelCapabilities("deepseek-v4-flash"))
            .Returns(new ModelCapabilities
            {
                ContextWindow = 1_000_000,
                SupportsPrefixCache = true,
                CacheControlType = CacheControlType.Ephemeral,
                SupportsCacheAlignedSummary = true,
            });

        var summarizer = new LlmContextSummarizer(provider.Object, primaryModel: "deepseek-v4-flash");
        var opts = new ContextManagerOptions { CompressionModel = "deepseek-v4-flash" };

        var removed = new List<Message>
        {
            Message.User("do the thing"),
            Message.Assistant("doing it"),
        };

        var result = await summarizer.SummarizeAsync(removed, opts);

        result.Summary.Should().Be("aligned-ok");

        captured.Should().NotBeNull();
        // Cache-aligned path: system is null, messages contain original messages + instruction
        captured!.SystemPrompt.Should().BeNull("cache-aligned path uses no system prompt");
        captured.Messages.Should().HaveCount(3); // original 2 + instruction
        captured.Messages[0].Role.Should().Be(MessageRole.User);
        captured.Messages[0].Content.OfType<TextContent>().First().Text.Should().Contain("do the thing");
        captured.Messages[1].Role.Should().Be(MessageRole.Assistant);
        captured.Messages[^1].Role.Should().Be(MessageRole.User);
        captured.Messages[^1].Content.OfType<TextContent>().First().Text.Should().Contain("Summarize");
    }

    [Fact]
    public async Task Summarize_UsesFallbackPath_WhenCapabilitiesNotSupported()
    {
        ModelRequest? captured = null;
        var provider = new Mock<IModelProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ModelRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(ResponseWith("<summary>fallback-ok</summary>"));

        // Claude supports prefix cache but NOT cache-aligned summary
        provider.Setup(p => p.GetModelCapabilities("claude-sonnet-4-6-20250901"))
            .Returns(new ModelCapabilities
            {
                ContextWindow = 200_000,
                SupportsPrefixCache = true,
                CacheControlType = CacheControlType.Ephemeral,
                SupportsCacheAlignedSummary = false,
            });

        var summarizer = new LlmContextSummarizer(provider.Object, primaryModel: "claude-sonnet-4-6-20250901");
        var opts = new ContextManagerOptions { CompressionModel = "claude-sonnet-4-6-20250901" };

        var removed = new List<Message>
        {
            Message.User("do the thing"),
            Message.Assistant("doing it"),
        };

        await summarizer.SummarizeAsync(removed, opts);

        captured.Should().NotBeNull();
        // Fallback path: has system prompt, single user message with formatted text
        captured!.SystemPrompt.Should().NotBeNull("fallback path uses system prompt");
        captured.Messages.Should().HaveCount(1); // Only the formatted user message
    }

    [Fact]
    public async Task Summarize_CacheAlignedPath_HandlesEmptyBatchGracefully()
    {
        var provider = new Mock<IModelProvider>();
        provider.Setup(p => p.GetModelCapabilities("deepseek-v4-pro"))
            .Returns(new ModelCapabilities
            {
                ContextWindow = 1_000_000,
                SupportsCacheAlignedSummary = true,
            });

        var summarizer = new LlmContextSummarizer(provider.Object, primaryModel: "deepseek-v4-pro");
        var opts = new ContextManagerOptions { CompressionModel = "deepseek-v4-pro" };

        var result = await summarizer.SummarizeAsync([], opts);

        result.Summary.Should().Be(string.Empty);
        // Verify the provider was never called (0-message batch short-circuits)
        provider.Verify(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
