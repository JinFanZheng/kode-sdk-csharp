using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Types;
using Moq;
using Xunit;

namespace Kode.Agent.Tests.Unit.Context;

/// <summary>
/// Tests for compression enabled flag and retry behavior.
/// </summary>
public sealed class ContextManagerEnabledAndRetryTests
{
    // ── Enabled flag ─────────────────────────────────────────────────────────

    [Fact]
    public void Analyze_WhenEnabledFalse_DoesNotCompress()
    {
        var manager = CreateManager(new ContextManagerOptions
        {
            Enabled = false,
            MaxTokens = 10, // very low threshold
        });

        // Many messages that would normally trigger compression
        var messages = new List<Message>();
        for (var i = 0; i < 20; i++)
            messages.Add(Message.User($"message number {i}"));

        var usage = manager.Analyze(messages);

        usage.ShouldCompress.Should().BeFalse("compression is disabled");
    }

    [Fact]
    public void Analyze_WhenEnabledTrue_CompressesNormally()
    {
        var manager = CreateManager(new ContextManagerOptions
        {
            Enabled = true,
            MaxTokens = 10,
        });

        var messages = new List<Message>();
        for (var i = 0; i < 100; i++)
            messages.Add(Message.User($"message number {i}"));

        var usage = manager.Analyze(messages);

        usage.ShouldCompress.Should().BeTrue("compression is enabled and token count exceeds threshold");
    }

    [Fact]
    public async Task CompressAsync_WhenEnabledFalse_ReturnsNull()
    {
        var summarizer = new StaticContextSummarizer();
        var manager = CreateManager(new ContextManagerOptions
        {
            Enabled = false,
            MaxTokens = 1,
            CompressToTokens = 50,
            MinRecentMessages = 2,
        }, summarizer);

        var messages = new List<Message>
        {
            Message.User("msg1"), Message.User("msg2"), Message.User("msg3"),
            Message.User("msg4"), Message.User("msg5"), Message.User("msg6"),
        };

        var result = await manager.CompressAsync(messages, []);

        result.Should().BeNull("compression is disabled, even with many messages");
    }

    // ── Retry behavior ───────────────────────────────────────────────────────

    [Fact]
    public async Task LlmSummarizer_RetriesOnTransientError()
    {
        var callCount = 0;
        var provider = new Mock<IModelProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()))
            .Returns<ModelRequest, CancellationToken>((_, _) =>
            {
                callCount++;
                if (callCount < 3)
                    throw new HttpRequestException("503 Service Unavailable",
                        inner: null, statusCode: System.Net.HttpStatusCode.ServiceUnavailable);
                return Task.FromResult(new ModelResponse
                {
                    Content = [new TextContent { Text = "<summary>retry-ok</summary>" }],
                    StopReason = ModelStopReason.EndTurn,
                    Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 },
                    Model = "test-model"
                });
            });

        var summarizer = new LlmContextSummarizer(provider.Object, primaryModel: "test-model");
        var result = await summarizer.SummarizeAsync(
            [Message.User("hi")],
            new ContextManagerOptions { CompressionModel = "test-model" });

        result.Summary.Should().Be("retry-ok");
        callCount.Should().BeGreaterThanOrEqualTo(3, "should retry until success");
    }

    [Fact]
    public async Task LlmSummarizer_GivesUpAfterMaxRetries()
    {
        var provider = new Mock<IModelProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("500 Internal Server Error",
                inner: null, statusCode: System.Net.HttpStatusCode.InternalServerError));

        var summarizer = new LlmContextSummarizer(provider.Object, primaryModel: "test-model");
        var result = await summarizer.SummarizeAsync(
            [Message.User("hi")],
            new ContextManagerOptions { CompressionModel = "test-model" });

        // Should fall back to static summarizer after exhausting retries
        result.Summary.Should().NotBeNullOrWhiteSpace();
        provider.Verify(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3), "should try 3 times then give up");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ContextManager CreateManager(
        ContextManagerOptions? options = null,
        IContextSummarizer? summarizer = null)
    {
        var storeMock = new Mock<IAgentStore>();
        storeMock.Setup(s => s.SaveHistoryWindowAsync(It.IsAny<string>(), It.IsAny<HistoryWindow>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);
        storeMock.Setup(s => s.SaveCompressionRecordAsync(It.IsAny<string>(), It.IsAny<CompressionRecord>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        return new ContextManager(storeMock.Object, "test-agent", options, summarizer);
    }
}
