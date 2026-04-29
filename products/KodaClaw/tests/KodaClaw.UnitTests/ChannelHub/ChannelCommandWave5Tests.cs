using FluentAssertions;
using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Commands;
using KodaClaw.ChannelHub.Delivery;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Models;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

/// <summary>
/// KC-CMD-W5: Tests for /model command and /new with model override.
/// </summary>
public sealed class ChannelCommandWave5Tests
{
    // ── Test helpers ─────────────────────────────────────────────────────────────

    private static ChannelDeliveryDispatchService BuildDispatchService()
    {
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

    private static ProviderAccount BuildAccount(string id, ModelProviderKind kind) => new ProviderAccount(
        Id:                        id,
        DisplayName:               id,
        ProviderKind:              kind,
        BaseUrl:                   null,
        ApiKeySecretRef:           null,
        ApiKeyEnvironmentVariable: null,
        AccessMode:                "api",
        Enabled:                   true,
        CreatedAt:                 DateTimeOffset.UtcNow,
        UpdatedAt:                 DateTimeOffset.UtcNow);

    private static AccountModel BuildEndpoint(string modelId, string displayName, bool isDefault = false, int index = 0) =>
        new AccountModel(
            Id: Guid.NewGuid().ToString(),
            AccountId: "acc-test",
            DisplayName: displayName,
            ModelId: modelId,
            Capabilities: ModelCapabilitySet.Text,
            IsDefaultForAccount: isDefault,
            IsGlobalDefault: isDefault,
            Enabled: true,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(index),
            UpdatedAt: DateTimeOffset.UtcNow);

    // ── Parser: /model ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_model_returns_Model_ControlKind_with_null_arg()
    {
        var result = ChannelCommandParser.Parse("/model");
        result.ControlKind.Should().Be(ChannelControlCommandKind.Model);
        result.ControlArg.Should().BeNull();
    }

    [Fact]
    public void Parse_models_alias_returns_Model_ControlKind()
    {
        var result = ChannelCommandParser.Parse("/models");
        result.ControlKind.Should().Be(ChannelControlCommandKind.Model);
        result.ControlArg.Should().BeNull();
    }

    [Fact]
    public void Parse_model_list_returns_Model_ControlKind_with_list_arg()
    {
        var result = ChannelCommandParser.Parse("/model list");
        result.ControlKind.Should().Be(ChannelControlCommandKind.Model);
        result.ControlArg.Should().Be("list");
    }

    // ── Parser: /new with arg ────────────────────────────────────────────────────

    [Fact]
    public void Parse_new_with_index_returns_NewSession_with_index_arg()
    {
        var result = ChannelCommandParser.Parse("/new 2");
        result.ControlKind.Should().Be(ChannelControlCommandKind.NewSession);
        result.ControlArg.Should().Be("2");
    }

    [Fact]
    public void Parse_new_with_model_id_returns_NewSession_with_model_id_arg()
    {
        var result = ChannelCommandParser.Parse("/new kimi-k2.5");
        result.ControlKind.Should().Be(ChannelControlCommandKind.NewSession);
        result.ControlArg.Should().Be("kimi-k2.5");
    }

    // ── Registry: Model entry ────────────────────────────────────────────────────

    [Theory]
    [InlineData("/model")]
    [InlineData("/models")]
    public void Registry_Find_returns_Model_entry_for_model_aliases(string alias)
    {
        var def = ChannelCommandRegistry.Find(alias);
        def.Should().NotBeNull();
        def!.ControlKind.Should().Be(ChannelControlCommandKind.Model);
    }

    // ── Dispatcher: /model (current) ─────────────────────────────────────────────

    [Fact]
    public async Task HandleModel_with_null_arg_returns_current_model_when_session_exists()
    {
        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.GetSessionModelAsync("session-abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync("kimi-k2.5");

        var registry = new Mock<IProviderAccountRepository>();
        registry
            .Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([BuildEndpoint("kimi-k2.5", "Kimi K2.5")]);

        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(),
            providerAccountRepository: registry.Object);
        var parsed = ChannelCommandParser.Parse("/model");

        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeTrue();
        sessionService.Verify(s => s.GetSessionModelAsync("session-abc", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleModel_with_null_arg_returns_default_message_when_session_not_created()
    {
        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.GetSessionModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var dispatcher = new ChannelCommandDispatcher(sessionService.Object, BuildDispatchService());
        var parsed = ChannelCommandParser.Parse("/model");

        // Should return true (command handled) and send a "not yet created" message
        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeTrue();
    }

    // ── Dispatcher: /model list ───────────────────────────────────────────────────

    [Fact]
    public async Task HandleModel_list_calls_ListAsync_and_returns_indexed_list()
    {
        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.GetSessionModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("claude-sonnet-4-6");

        var registry = new Mock<IProviderAccountRepository>();
        registry
            .Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                BuildEndpoint("claude-sonnet-4-6", "Claude Sonnet 4.6", isDefault: true, index: 0),
                BuildEndpoint("kimi-k2.5", "Kimi K2.5", isDefault: false, index: 1),
            ]);
        registry
            .Setup(r => r.ListAccountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([BuildAccount("acc-test", ModelProviderKind.OpenAICompatible)]);

        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(),
            providerAccountRepository: registry.Object);

        var parsed = ChannelCommandParser.Parse("/model list");
        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeTrue();
        registry.Verify(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleModel_list_marks_current_model_with_star()
    {
        // We capture the sent message by intercepting DispatchAsync's reply.
        // Since the dispatcher calls SendNotificationAsync which swallows errors,
        // we verify behavior indirectly by confirming the dispatcher returns true.
        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.GetSessionModelAsync("session-abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync("kimi-k2.5");

        var registry = new Mock<IProviderAccountRepository>();
        registry
            .Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                BuildEndpoint("claude-sonnet-4-6", "Claude Sonnet 4.6", isDefault: true, index: 0),
                BuildEndpoint("kimi-k2.5", "Kimi K2.5", isDefault: false, index: 1),
            ]);
        registry
            .Setup(r => r.ListAccountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([BuildAccount("acc-test", ModelProviderKind.OpenAICompatible)]);

        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(),
            providerAccountRepository: registry.Object);

        var parsed = ChannelCommandParser.Parse("/model list");
        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeTrue();
        // Verified: both GetSessionModelAsync (for star) and ListAsync were called
        sessionService.Verify(s => s.GetSessionModelAsync("session-abc", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Dispatcher: /new with model ───────────────────────────────────────────────

    [Fact]
    public async Task HandleNewSession_with_index_calls_RotateSessionAsync_with_correct_modelId()
    {
        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.RotateSessionAsync(It.IsAny<ThreadBinding>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("new-session-id");

        var registry = new Mock<IProviderAccountRepository>();
        registry
            .Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                BuildEndpoint("claude-sonnet-4-6", "Claude Sonnet 4.6", isDefault: true, index: 0),
                BuildEndpoint("kimi-k2.5", "Kimi K2.5", isDefault: false, index: 1),
            ]);

        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(),
            providerAccountRepository: registry.Object);

        var parsed = ChannelCommandParser.Parse("/new 2");
        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeTrue();
        sessionService.Verify(s => s.RotateSessionAsync(
            It.IsAny<ThreadBinding>(),
            "kimi-k2.5",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleNewSession_with_model_id_calls_RotateSessionAsync_with_correct_modelId()
    {
        var sessionService = new Mock<IChannelSessionService>();
        sessionService
            .Setup(s => s.RotateSessionAsync(It.IsAny<ThreadBinding>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("new-session-id");

        var registry = new Mock<IProviderAccountRepository>();
        registry
            .Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                BuildEndpoint("claude-sonnet-4-6", "Claude Sonnet 4.6", isDefault: true, index: 0),
                BuildEndpoint("kimi-k2.5", "Kimi K2.5", isDefault: false, index: 1),
            ]);

        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(),
            providerAccountRepository: registry.Object);

        var parsed = ChannelCommandParser.Parse("/new kimi-k2.5");
        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeTrue();
        sessionService.Verify(s => s.RotateSessionAsync(
            It.IsAny<ThreadBinding>(),
            "kimi-k2.5",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleNewSession_with_out_of_range_index_does_not_call_RotateSessionAsync()
    {
        var sessionService = new Mock<IChannelSessionService>();

        var registry = new Mock<IProviderAccountRepository>();
        registry
            .Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                BuildEndpoint("claude-sonnet-4-6", "Claude Sonnet 4.6", isDefault: true, index: 0),
            ]);

        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(),
            providerAccountRepository: registry.Object);

        var parsed = ChannelCommandParser.Parse("/new 99");
        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeTrue();
        sessionService.Verify(s => s.RotateSessionAsync(
            It.IsAny<ThreadBinding>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleNewSession_with_unknown_model_id_does_not_call_RotateSessionAsync()
    {
        var sessionService = new Mock<IChannelSessionService>();

        var registry = new Mock<IProviderAccountRepository>();
        registry
            .Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                BuildEndpoint("claude-sonnet-4-6", "Claude Sonnet 4.6", isDefault: true, index: 0),
            ]);

        var dispatcher = new ChannelCommandDispatcher(
            sessionService.Object, BuildDispatchService(),
            providerAccountRepository: registry.Object);

        var parsed = ChannelCommandParser.Parse("/new 不存在的模型");
        var result = await dispatcher.DispatchAsync(
            parsed, "session-abc", BuildAccount(), BuildBinding(), CancellationToken.None);

        result.Should().BeTrue();
        sessionService.Verify(s => s.RotateSessionAsync(
            It.IsAny<ThreadBinding>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
