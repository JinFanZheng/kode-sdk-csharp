using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Agent;
using Kode.Agent.Sdk.Core.Types;
using Moq;
using Xunit;

namespace Kode.Agent.Tests.Unit;

public sealed class LlmToolResultCompressorTests
{
    private static ModelResponse OkResponse(string text) => new()
    {
        Content = [new TextContent { Text = text }],
        StopReason = ModelStopReason.EndTurn,
        Usage = new TokenUsage { InputTokens = 0, OutputTokens = 0 },
        Model = "fake"
    };

    [Fact]
    public async Task CompressIfNeeded_SkipsWhenResultFailed()
    {
        var provider = new Mock<IModelProvider>(MockBehavior.Strict);
        var compressor = new LlmToolResultCompressor(provider.Object);

        var input = ToolResult.Fail("boom");
        var result = await compressor.CompressIfNeededAsync(
            "bash_run", input, [], new ToolResultCompressionOptions { Enabled = true, ThresholdBytes = 10 });

        result.Should().BeSameAs(input);
        provider.Verify(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompressIfNeeded_SkipsWhenBelowThreshold()
    {
        var provider = new Mock<IModelProvider>(MockBehavior.Strict);
        var compressor = new LlmToolResultCompressor(provider.Object);

        var input = ToolResult.Ok(new { stdout = "short" });
        var result = await compressor.CompressIfNeededAsync(
            "bash_run", input, [], new ToolResultCompressionOptions { Enabled = true, ThresholdBytes = 10_000 });

        result.Should().BeSameAs(input);
        provider.Verify(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompressIfNeeded_SkipsNonCompressibleTool()
    {
        var provider = new Mock<IModelProvider>(MockBehavior.Strict);
        var compressor = new LlmToolResultCompressor(provider.Object);

        var bigPayload = new string('x', 20_000);
        var input = ToolResult.Ok(bigPayload);
        var result = await compressor.CompressIfNeededAsync(
            "fs_read", input, [], new ToolResultCompressionOptions { Enabled = true, ThresholdBytes = 1_000 });

        result.Should().BeSameAs(input);
        provider.Verify(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompressIfNeeded_CompressesOversizedBashResult_AndSendsConcisePrompt()
    {
        ModelRequest? captured = null;
        var provider = new Mock<IModelProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ModelRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(OkResponse("Summary: large bash ran successfully with 42 files processed."));

        var compressor = new LlmToolResultCompressor(provider.Object, primaryModel: "haiku");

        var bigPayload = new string('y', 60_000);
        var input = ToolResult.Ok(bigPayload);

        var result = await compressor.CompressIfNeededAsync(
            "bash_run",
            input,
            [Message.User("please run the thing")],
            new ToolResultCompressionOptions { Enabled = true, ThresholdBytes = 50_000 });

        result.Success.Should().BeTrue();
        // Prompt budget + directive integrity
        captured.Should().NotBeNull();
        captured!.MaxTokens.Should().Be(600);
        var sys = captured.SystemPrompt ?? "";
        sys.Should().NotBeNullOrWhiteSpace();
        var sysLower = sys.ToLowerInvariant();
        sysLower.Should().Contain("summar", because: "prompt must describe a summarization task");
        sys.Should().MatchRegex(@"\b300\b", because: "word budget should be explicit");
        sysLower.Should().Contain("preserve",
            because: "instruction to keep key facts (errors/paths/codes) must persist");
    }

    [Fact]
    public async Task CompressIfNeeded_FallsBackToTruncationOnLlmFailure()
    {
        var provider = new Mock<IModelProvider>();
        provider.Setup(p => p.CompleteAsync(It.IsAny<ModelRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("llm down"));
        var compressor = new LlmToolResultCompressor(provider.Object);

        var bigPayload = new string('z', 60_000);
        var input = ToolResult.Ok(bigPayload);

        var result = await compressor.CompressIfNeededAsync(
            "bash_run", input, [],
            new ToolResultCompressionOptions { Enabled = true, ThresholdBytes = 1_000 });

        result.Success.Should().BeTrue();
        // Fallback struct contains truncated = true
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        json.Should().Contain("\"truncated\":true");
    }
}
