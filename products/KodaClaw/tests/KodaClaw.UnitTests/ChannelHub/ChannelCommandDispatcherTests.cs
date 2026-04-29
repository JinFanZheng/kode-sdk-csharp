using FluentAssertions;
using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Commands;
using KodaClaw.ChannelHub.Delivery;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

/// <summary>
/// KC-CMD-W2: Unit tests for ChannelCommandDispatcher.
/// </summary>
public sealed class ChannelCommandDispatcherTests
{
    // ── Test helpers ─────────────────────────────────────────────────────────────

    private static ChannelDeliveryDispatchService BuildDispatchService()
    {
        // Create a real ChannelDeliveryDispatchService with mocked deps.
        // SendNotificationAsync will fail gracefully (connector not found);
        // DispatchAsync swallows those exceptions, so this is fine for testing.
        var repo = new Mock<IThreadBindingRepository>();
        var resolver = new ChannelConnectorKindResolver([]);
        return new ChannelDeliveryDispatchService(repo.Object, resolver);
    }

    private static ChannelAccount BuildAccount() => new ChannelAccount(
        Id: "acc-001",
        ConnectorKind: ChannelConnectorKind.Telegram,
        DisplayName: "KodaBot",
        State: ChannelAccountState.Connected,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    private static ThreadBinding BuildBinding() => new ThreadBinding(
        Id: "binding-001",
        ConnectorKind: ChannelConnectorKind.Telegram,
        AccountId: "acc-001",
        ExternalThreadId: "tg-thread-1",
        ThreadType: ChannelThreadType.DirectMessage,
        SessionId: "session-abc",
        SessionKind: SessionKind.ChannelDirectMessage,
        ChannelIdentity: new ChannelIdentity(Id: "user-001", DisplayName: "Test User"),
        PolicyId: "policy-001",
        DeliveryRuleId: "rule-001",
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    // ── False returns for non-control commands ───────────────────────────────────

    [Fact]
    public async Task DispatchAsync_returns_false_for_plain_text()
    {
        var sessionService = new Mock<IChannelSessionService>();
        var dispatcher = new ChannelCommandDispatcher(sessionService.Object, BuildDispatchService());
        var parsed = ChannelCommandParser.Parse("hello world");

        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeFalse();
        sessionService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchAsync_returns_false_for_directive_modifier()
    {
        var sessionService = new Mock<IChannelSessionService>();
        var dispatcher = new ChannelCommandDispatcher(sessionService.Object, BuildDispatchService());
        var parsed = ChannelCommandParser.Parse("/think 分析代码");

        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeFalse(because: "directives are not control commands");
    }

    // ── NewSession ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_new_session_calls_RotateSessionAsync()
    {
        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.RotateSessionAsync(It.IsAny<ThreadBinding>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("new-session-id");

        var dispatcher = new ChannelCommandDispatcher(sessionService.Object, BuildDispatchService());
        var parsed = ChannelCommandParser.Parse("/new");

        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeTrue();
        sessionService.Verify(s => s.RotateSessionAsync(
            It.IsAny<ThreadBinding>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Status ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_status_calls_GetSessionStateAsync()
    {
        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.GetSessionStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentSessionState?)null);

        var dispatcher = new ChannelCommandDispatcher(sessionService.Object, BuildDispatchService());
        var parsed = ChannelCommandParser.Parse("/status");

        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeTrue();
        sessionService.Verify(s => s.GetSessionStateAsync(
            "session-abc", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Stop ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_stop_calls_StopCurrentTurnAsync()
    {
        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.StopCurrentTurnAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("已中断。");

        var dispatcher = new ChannelCommandDispatcher(sessionService.Object, BuildDispatchService());
        var parsed = ChannelCommandParser.Parse("/stop");

        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeTrue();
        sessionService.Verify(s => s.StopCurrentTurnAsync(
            "session-abc", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Help ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_help_returns_true_without_calling_session_service()
    {
        var sessionService = new Mock<IChannelSessionService>();
        var dispatcher = new ChannelCommandDispatcher(sessionService.Object, BuildDispatchService());
        var parsed = ChannelCommandParser.Parse("/help");

        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeTrue();
        sessionService.VerifyNoOtherCalls();
    }

    // ── Status message formatting ─────────────────────────────────────────────────

    [Fact]
    public void FormatSessionStatusMessage_null_returns_unavailable_message()
    {
        var msg = ChannelCommandDispatcher.FormatSessionStatusMessage(null);
        msg.Should().NotBeNullOrWhiteSpace();
        msg.Should().Contain("不可用");
    }

    // ── ThinkToggle ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_think_on_persists_ThinkingEnabled_true()
    {
        var binding = BuildBinding();
        var repo = new Mock<IThreadBindingRepository>();
        repo.Setup(r => r.GetByIdAsync(binding.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(binding);

        var sessionService = new Mock<IChannelSessionService>();
        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(), bindingRepository: repo.Object);
        var parsed = ChannelCommandParser.Parse("/think on");

        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), binding, CancellationToken.None);

        result.Should().BeTrue();
        repo.Verify(r => r.UpsertAsync(
            It.Is<ThreadBinding>(b => b.ThinkingEnabled == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_think_off_persists_ThinkingEnabled_false()
    {
        var binding = BuildBinding() with { ThinkingEnabled = true };
        var repo = new Mock<IThreadBindingRepository>();
        repo.Setup(r => r.GetByIdAsync(binding.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(binding);

        var sessionService = new Mock<IChannelSessionService>();
        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(), bindingRepository: repo.Object);
        var parsed = ChannelCommandParser.Parse("/think off");

        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), binding, CancellationToken.None);

        result.Should().BeTrue();
        repo.Verify(r => r.UpsertAsync(
            It.Is<ThreadBinding>(b => b.ThinkingEnabled == false),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_bare_think_does_not_mutate_binding()
    {
        var binding = BuildBinding();
        var repo = new Mock<IThreadBindingRepository>();
        repo.Setup(r => r.GetByIdAsync(binding.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(binding);

        var sessionService = new Mock<IChannelSessionService>();
        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(), bindingRepository: repo.Object);
        var parsed = ChannelCommandParser.Parse("/think");

        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), binding, CancellationToken.None);

        result.Should().BeTrue();
        repo.Verify(r => r.UpsertAsync(It.IsAny<ThreadBinding>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_think_already_on_is_idempotent()
    {
        var binding = BuildBinding() with { ThinkingEnabled = true };
        var repo = new Mock<IThreadBindingRepository>();
        repo.Setup(r => r.GetByIdAsync(binding.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(binding);

        var sessionService = new Mock<IChannelSessionService>();
        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(), bindingRepository: repo.Object);
        var parsed = ChannelCommandParser.Parse("/think on");

        await dispatcher.DispatchAsync(parsed, "session-abc", BuildAccount(), binding, CancellationToken.None);

        repo.Verify(r => r.UpsertAsync(It.IsAny<ThreadBinding>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── StreamToggle ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_stream_on_persists_StreamOverride_true()
    {
        var binding = BuildBinding();
        var repo = new Mock<IThreadBindingRepository>();
        repo.Setup(r => r.GetByIdAsync(binding.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(binding);

        var sessionService = new Mock<IChannelSessionService>();
        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(), bindingRepository: repo.Object);
        var parsed = ChannelCommandParser.Parse("/stream on");

        await dispatcher.DispatchAsync(parsed, "session-abc", BuildAccount(), binding, CancellationToken.None);

        repo.Verify(r => r.UpsertAsync(
            It.Is<ThreadBinding>(b => b.StreamOverride == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_stream_off_persists_StreamOverride_false()
    {
        var binding = BuildBinding();
        var repo = new Mock<IThreadBindingRepository>();
        repo.Setup(r => r.GetByIdAsync(binding.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(binding);

        var sessionService = new Mock<IChannelSessionService>();
        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(), bindingRepository: repo.Object);
        var parsed = ChannelCommandParser.Parse("/stream off");

        await dispatcher.DispatchAsync(parsed, "session-abc", BuildAccount(), binding, CancellationToken.None);

        repo.Verify(r => r.UpsertAsync(
            It.Is<ThreadBinding>(b => b.StreamOverride == false),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_quiet_aliased_to_stream_toggle()
    {
        var binding = BuildBinding();
        var repo = new Mock<IThreadBindingRepository>();
        repo.Setup(r => r.GetByIdAsync(binding.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(binding);

        var sessionService = new Mock<IChannelSessionService>();
        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(), bindingRepository: repo.Object);
        var parsed = ChannelCommandParser.Parse("/quiet");

        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), binding, CancellationToken.None);

        // bare /quiet → ControlArg null → show state, no upsert
        result.Should().BeTrue();
        parsed.ControlKind.Should().Be(ChannelControlCommandKind.StreamToggle);
    }
}
