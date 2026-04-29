using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Tools;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class ChannelSendToolTests
{
    private readonly Mock<IChannelSendService> _sendServiceMock;
    private readonly ChannelSendTool _tool;

    public ChannelSendToolTests()
    {
        _sendServiceMock = new Mock<IChannelSendService>();
        _tool = new ChannelSendTool(_sendServiceMock.Object);
    }

    // ── Metadata ─────────────────────────────────────────────────────────

    [Fact]
    public void Name_ReturnsChannelSend()
        => _tool.Name.Should().Be("channel_send");

    [Fact]
    public void Attributes_ReadOnlyFalse_RequiresApprovalFalse()
    {
        _tool.Attributes.ReadOnly.Should().BeFalse();
        _tool.Attributes.RequiresApproval.Should().BeFalse();
    }

    // ── Successful send ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ValidArgs_CallsSendServiceAndReturnsOk()
    {
        var sentAt = DateTimeOffset.UtcNow;
        _sendServiceMock
            .Setup(s => s.SendAsync("binding-001", "Hello there!", null, null, It.IsAny<OutboundMessageFormat>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChannelSendResult(Ok: true, BindingId: "binding-001", SentAt: sentAt));

        var result = await ExecuteAsync(new ChannelSendArgs
        {
            BindingId = "binding-001",
            Text = "Hello there!",
        });

        result.Success.Should().BeTrue();
        _sendServiceMock.Verify(
            s => s.SendAsync("binding-001", "Hello there!", null, null, It.IsAny<OutboundMessageFormat>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Error propagation ────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SendServiceThrows_ReturnsToolError()
    {
        _sendServiceMock
            .Setup(s => s.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<OutboundMessageFormat>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Channel binding 'bad-id' was not found."));

        var result = await ExecuteAsync(new ChannelSendArgs
        {
            BindingId = "bad-id",
            Text = "Test",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Channel binding 'bad-id' was not found.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private Task<ToolResult> ExecuteAsync(ChannelSendArgs args)
    {
        var context = new ToolContext
        {
            AgentId = "test-agent",
            CallId = "test-call",
            Sandbox = new Mock<ISandbox>().Object,
        };
        return _tool.ExecuteAsync((object)args, context, CancellationToken.None);
    }
}
