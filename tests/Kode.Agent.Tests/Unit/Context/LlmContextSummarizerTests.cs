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
}
