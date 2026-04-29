using FluentAssertions;
using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Delivery;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Sessions;
using KodaClaw.ControlPlane;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.ChannelHub;

public sealed class ChannelDeliveryGovernanceIntegrationTests
{
    [Fact]
    public async Task Auto_send_should_not_create_approval_or_inbox_item()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-channel-delivery-integration");
        using var provider = CreateServiceProvider(workspace.Path);
        var service = provider.GetRequiredService<ChannelDeliveryGovernanceService>();
        var approvals = provider.GetRequiredService<IApprovalRepository>();
        var inbox = provider.GetRequiredService<IInboxRepository>();
        var now = new DateTimeOffset(2026, 3, 19, 15, 0, 0, TimeSpan.Zero);
        var binding = BuildBinding("binding-auto", now);
        var draft = BuildDraft(binding, DeliveryMode.AutoSend, now);
        var rule = new DeliveryRule(
            Id: "delivery-auto",
            Mode: DeliveryMode.AutoSend,
            UpdatedAt: now);

        var result = await service.EvaluateAsync(binding, rule, draft);

        result.Disposition.Should().Be(ChannelDeliveryDisposition.SendImmediately);
        result.ApprovalId.Should().BeNull();
        result.InboxItemId.Should().BeNull();

        var storedApprovals = await approvals.ListAsync(new ApprovalQuery(Kind: ApprovalKind.ChannelDelivery, Limit: 10));
        var storedInboxItems = await inbox.ListAsync(new InboxQuery(Kind: InboxItemKind.ChannelUpdate, Limit: 10));

        storedApprovals.Should().BeEmpty();
        storedInboxItems.Should().BeEmpty();
    }

    [Theory]
    [InlineData(DeliveryMode.DraftApproval)]
    [InlineData(DeliveryMode.RequireApproval)]
    public async Task Approval_modes_should_create_pending_approval_and_channel_update_inbox_item(DeliveryMode deliveryMode)
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-channel-delivery-integration");
        using var provider = CreateServiceProvider(workspace.Path);
        var service = provider.GetRequiredService<ChannelDeliveryGovernanceService>();
        var approvals = provider.GetRequiredService<IApprovalRepository>();
        var inbox = provider.GetRequiredService<IInboxRepository>();
        var now = new DateTimeOffset(2026, 3, 19, 16, 0, 0, TimeSpan.Zero);
        var binding = BuildBinding($"binding-{deliveryMode}", now);
        var draft = BuildDraft(binding, deliveryMode, now);
        var rule = new DeliveryRule(
            Id: $"delivery-{deliveryMode}",
            Mode: deliveryMode,
            UpdatedAt: now,
            AllowProactiveSend: false);

        var result = await service.EvaluateAsync(binding, rule, draft);

        result.Disposition.Should().Be(ChannelDeliveryDisposition.ApprovalRequired);
        result.ApprovalId.Should().NotBeNull();
        result.InboxItemId.Should().NotBeNull();

        var approval = await approvals.GetByIdAsync(result.ApprovalId!);
        var inboxItem = await inbox.GetByIdAsync(result.InboxItemId!);

        approval.Should().NotBeNull();
        approval!.Kind.Should().Be(ApprovalKind.ChannelDelivery);
        approval.Status.Should().Be(ApprovalStatus.Pending);
        approval.SessionId.Should().Be(binding.SessionId);
        approval.InboxItemId.Should().Be(result.InboxItemId);
        approval.PayloadJson.Should().Contain(deliveryMode.ToString());

        inboxItem.Should().NotBeNull();
        inboxItem!.Kind.Should().Be(InboxItemKind.ChannelUpdate);
        inboxItem.Status.Should().Be(InboxItemStatus.Open);
        inboxItem.RequiresAction.Should().BeTrue();
        inboxItem.ApprovalId.Should().Be(result.ApprovalId);
        inboxItem.Route.Should().Be($"/approvals/{result.ApprovalId}");
        inboxItem.PayloadJson.Should().Contain(draft.DraftId);
    }

    [Fact]
    public async Task Delivery_request_should_be_rejected_when_draft_does_not_match_binding()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-channel-delivery-integration");
        using var provider = CreateServiceProvider(workspace.Path);
        var service = provider.GetRequiredService<ChannelDeliveryGovernanceService>();
        var now = new DateTimeOffset(2026, 3, 19, 17, 0, 0, TimeSpan.Zero);
        var binding = BuildBinding("binding-invalid", now);
        var draft = BuildDraft(binding, DeliveryMode.RequireApproval, now) with
        {
            AccountId = "different-account",
        };
        var rule = new DeliveryRule(
            Id: "delivery-require-approval",
            Mode: DeliveryMode.RequireApproval,
            UpdatedAt: now);

        var action = () => service.EvaluateAsync(binding, rule, draft);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    private static ServiceProvider CreateServiceProvider(string workspaceRoot)
    {
        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        services.AddKodaClawJsonStore(workspaceRoot);
        services.AddKodaClawControlPlane();
        services.AddKodaClawChannelHub();
        return services.BuildServiceProvider();
    }

    private static ThreadBinding BuildBinding(string id, DateTimeOffset timestamp)
    {
        return new ThreadBinding(
            Id: id,
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: "telegram-main",
            ExternalThreadId: $"chat-{id}",
            ThreadType: ChannelThreadType.DirectMessage,
            SessionId: $"session-{id}",
            SessionKind: SessionKind.ChannelDirectMessage,
            ChannelIdentity: new ChannelIdentity(
                Id: $"user-{id}",
                Username: $"user_{id}",
                DisplayName: $"User {id}"),
            PolicyId: "policy-default-dm",
            DeliveryRuleId: "delivery-default-dm",
            CreatedAt: timestamp,
            UpdatedAt: timestamp,
            LastInboundAt: timestamp.AddMinutes(-1),
            LastMessagePreview: "latest inbound");
    }

    private static ChannelOutboundDraft BuildDraft(
        ThreadBinding binding,
        DeliveryMode deliveryMode,
        DateTimeOffset timestamp)
    {
        return new ChannelOutboundDraft(
            DraftId: $"draft-{binding.Id}-{deliveryMode}",
            BindingId: binding.Id,
            ConnectorKind: binding.ConnectorKind,
            AccountId: binding.AccountId,
            ExternalThreadId: binding.ExternalThreadId,
            MessageText: $"Reply for {binding.ExternalThreadId}",
            DeliveryMode: deliveryMode,
            CreatedAt: timestamp,
            SessionId: binding.SessionId,
            CorrelationId: $"corr-{binding.Id}-{deliveryMode}");
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
