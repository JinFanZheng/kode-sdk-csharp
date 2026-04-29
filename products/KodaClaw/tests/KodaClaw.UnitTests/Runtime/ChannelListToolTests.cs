using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Tools;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class ChannelListToolTests
{
    private readonly Mock<IThreadBindingRepository> _bindingRepositoryMock;
    private readonly ChannelListTool _tool;

    public ChannelListToolTests()
    {
        _bindingRepositoryMock = new Mock<IThreadBindingRepository>();
        _tool = new ChannelListTool(_bindingRepositoryMock.Object);
    }

    // ── Metadata ──────────────────────────────────────────────────────────────

    [Fact]
    public void Name_ReturnsChannelList()
        => _tool.Name.Should().Be("channel_list");

    [Fact]
    public void Attributes_ReadOnlyTrue()
        => _tool.Attributes.ReadOnly.Should().BeTrue();

    // ── List all bindings ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NoFilter_ReturnsAllBindings()
    {
        var now = DateTimeOffset.UtcNow;
        var bindings = new List<ThreadBinding>
        {
            CreateBinding("binding-001", ChannelConnectorKind.Telegram, ChannelThreadType.DirectMessage, "Alice", now),
            CreateBinding("binding-002", ChannelConnectorKind.GenericWebhook, ChannelThreadType.Group, "Ops Bridge", now),
        };
        _bindingRepositoryMock
            .Setup(r => r.ListAsync(It.IsAny<ChannelQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(bindings);

        var result = await ExecuteAsync(new ChannelListArgs());

        result.Success.Should().BeTrue();
        _bindingRepositoryMock.Verify(
            r => r.ListAsync(It.IsAny<ChannelQuery>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Filter by connector kind ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithConnectorKindFilter_PassesQueryToRepository()
    {
        ChannelQuery? capturedQuery = null;
        _bindingRepositoryMock
            .Setup(r => r.ListAsync(It.IsAny<ChannelQuery>(), It.IsAny<CancellationToken>()))
            .Callback<ChannelQuery?, CancellationToken>((q, _) => capturedQuery = q)
            .ReturnsAsync(new List<ThreadBinding>());

        var result = await ExecuteAsync(new ChannelListArgs { ConnectorKind = "Telegram" });

        result.Success.Should().BeTrue();
        capturedQuery.Should().NotBeNull();
        capturedQuery!.ConnectorKind.Should().Be(ChannelConnectorKind.Telegram);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<ToolResult> ExecuteAsync(ChannelListArgs args)
    {
        var context = new ToolContext
        {
            AgentId = "test-agent",
            CallId = "test-call",
            Sandbox = new Mock<ISandbox>().Object,
        };
        return _tool.ExecuteAsync((object)args, context, CancellationToken.None);
    }

    private static ThreadBinding CreateBinding(
        string id,
        ChannelConnectorKind connectorKind,
        ChannelThreadType threadType,
        string displayName,
        DateTimeOffset now)
    {
        var sessionKind = threadType == ChannelThreadType.DirectMessage
            ? SessionKind.ChannelDirectMessage
            : SessionKind.ChannelGroup;

        return new ThreadBinding(
            Id: id,
            ConnectorKind: connectorKind,
            AccountId: "account-001",
            ExternalThreadId: "ext-001",
            ThreadType: threadType,
            SessionId: $"session-{id}",
            SessionKind: sessionKind,
            ChannelIdentity: new ChannelIdentity(Id: "user-001", DisplayName: displayName),
            PolicyId: "policy-001",
            DeliveryRuleId: "rule-001",
            CreatedAt: now,
            UpdatedAt: now,
            LastInboundAt: now);
    }
}
