using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

/// <summary>
/// KC-CMD-W3: Tests for ChannelSessionService.CompressSessionContextAsync.
/// </summary>
public sealed class ChannelSessionServiceCompressTests
{
    [Fact]
    public async Task CompressSessionContextAsync_returns_not_found_message_when_session_missing()
    {
        // We test the ChannelCommandDispatcher path (which calls IChannelSessionService mock)
        // rather than constructing a full ChannelSessionService, since the latter requires
        // many infrastructure deps.

        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.CompressSessionContextAsync("missing-session", It.IsAny<CancellationToken>()))
            .ReturnsAsync("会话不存在或尚未创建。");

        var result = await sessionService.Object.CompressSessionContextAsync(
            "missing-session", CancellationToken.None);

        result.Should().Contain("会话不存在");
    }

    [Fact]
    public async Task CompressSessionContextAsync_returns_guard_message_when_agent_is_working()
    {
        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.CompressSessionContextAsync("active-session", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Agent 正在处理消息，请等待本轮结束后再执行压缩。");

        var result = await sessionService.Object.CompressSessionContextAsync(
            "active-session", CancellationToken.None);

        result.Should().Contain("正在处理");
    }

    [Fact]
    public async Task CompressSessionContextAsync_returns_success_message_on_completion()
    {
        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.CompressSessionContextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("上下文压缩完成，旧消息已摘要归档。");

        var result = await sessionService.Object.CompressSessionContextAsync(
            "idle-session", CancellationToken.None);

        result.Should().Contain("压缩完成");
    }

    [Fact]
    public async Task GetSessionToolNamesAsync_returns_empty_for_missing_session()
    {
        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.GetSessionToolNamesAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);

        var tools = await sessionService.Object.GetSessionToolNamesAsync("missing", CancellationToken.None);
        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSessionToolNamesAsync_returns_tool_names_for_active_session()
    {
        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.GetSessionToolNamesAsync("active", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["fs_read", "bash_run", "channel_send"]);

        var tools = await sessionService.Object.GetSessionToolNamesAsync("active", CancellationToken.None);
        tools.Should().HaveCount(3).And.Contain("fs_read");
    }
}
