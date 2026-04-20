using System.Text.Json;
using FluentAssertions;
using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Connectors.Telegram;
using KodaClaw.Contracts;
using KodaClaw.ControlPlane;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.ChannelHub;

public sealed class ChannelDeliveryApprovalIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Approve_should_send_telegram_draft_and_append_delivery_audit()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-channel-delivery-approval");
        var fakeTelegram = new FakeTelegramApiClient();
        using var provider = CreateServiceProvider(workspace.Path, fakeTelegram);
        var accounts = provider.GetRequiredService<IChannelAccountRepository>();
        var bindings = provider.GetRequiredService<IThreadBindingRepository>();
        var approvals = provider.GetRequiredService<IApprovalRepository>();
        var inbox = provider.GetRequiredService<IInboxRepository>();
        var audit = provider.GetRequiredService<IChannelAuditRepository>();
        var governance = provider.GetRequiredService<ChannelDeliveryGovernanceService>();
        var approvalService = provider.GetRequiredService<ChannelDeliveryApprovalService>();
        var now = new DateTimeOffset(2026, 3, 19, 18, 0, 0, TimeSpan.Zero);

        var account = BuildAccount(ChannelConnectorKind.Telegram, "telegram-main", now) with
        {
            CredentialReference = "inline:telegram-token-approval"
        };
        var binding = BuildBinding(
            id: "binding-telegram-approval",
            connectorKind: ChannelConnectorKind.Telegram,
            threadType: ChannelThreadType.DirectMessage,
            timestamp: now);
        var draft = BuildDraft(binding, DeliveryMode.DraftApproval, now);

        await accounts.UpsertAsync(account);
        await bindings.UpsertAsync(binding);

        var evaluation = await governance.EvaluateAsync(
            binding,
            new DeliveryRule("delivery-default-dm", DeliveryMode.DraftApproval, now),
            draft);
        var pendingApproval = await approvals.GetByIdAsync(evaluation.ApprovalId!);

        pendingApproval.Should().NotBeNull();
        var result = await approvalService.ApproveAsync(pendingApproval!, "approve telegram send");

        result.Status.Should().Be(ChannelDeliveryApprovalDispatchStatus.Completed);
        result.Approval.Should().NotBeNull();
        result.Approval!.Status.Should().Be(ApprovalStatus.Approved);
        result.Approval.DecisionNote.Should().Be("approve telegram send");

        fakeTelegram.SendCalls.Should().ContainSingle();
        fakeTelegram.SendCalls[0].Token.Should().Be("telegram-token-approval");
        fakeTelegram.SendCalls[0].ChatId.Should().Be(10001);
        fakeTelegram.SendCalls[0].Text.Should().Be(draft.MessageText);

        var resolvedInbox = await inbox.GetByIdAsync(evaluation.InboxItemId!);
        resolvedInbox.Should().NotBeNull();
        resolvedInbox!.Status.Should().Be(InboxItemStatus.Resolved);
        resolvedInbox.ResolvedAt.Should().NotBeNull();

        var updatedBinding = await bindings.GetByIdAsync(binding.Id);
        updatedBinding.Should().NotBeNull();
        updatedBinding!.LastOutboundAt.Should().NotBeNull();
        updatedBinding.LastMessagePreview.Should().Be(draft.MessageText);

        var entries = await audit.ListByBindingIdAsync(binding.Id, 20);
        entries.Select(item => item.EventType).Should().Contain(new[]
        {
            "approval.approved",
            "delivery.sent",
        });
    }

    [Fact]
    public async Task Reject_should_resolve_approval_without_sending_and_append_rejection_audit()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-channel-delivery-approval");
        var fakeTelegram = new FakeTelegramApiClient();
        using var provider = CreateServiceProvider(workspace.Path, fakeTelegram);
        var accounts = provider.GetRequiredService<IChannelAccountRepository>();
        var bindings = provider.GetRequiredService<IThreadBindingRepository>();
        var approvals = provider.GetRequiredService<IApprovalRepository>();
        var inbox = provider.GetRequiredService<IInboxRepository>();
        var audit = provider.GetRequiredService<IChannelAuditRepository>();
        var governance = provider.GetRequiredService<ChannelDeliveryGovernanceService>();
        var approvalService = provider.GetRequiredService<ChannelDeliveryApprovalService>();
        var now = new DateTimeOffset(2026, 3, 19, 18, 30, 0, TimeSpan.Zero);

        var account = BuildAccount(ChannelConnectorKind.GenericWebhook, "webhook-group", now);
        var binding = BuildBinding(
            id: "binding-group-reject",
            connectorKind: ChannelConnectorKind.GenericWebhook,
            threadType: ChannelThreadType.Group,
            timestamp: now);
        var draft = BuildDraft(binding, DeliveryMode.RequireApproval, now);

        await accounts.UpsertAsync(account);
        await bindings.UpsertAsync(binding);

        var evaluation = await governance.EvaluateAsync(
            binding,
            new DeliveryRule("delivery-default-group", DeliveryMode.RequireApproval, now),
            draft);
        var pendingApproval = await approvals.GetByIdAsync(evaluation.ApprovalId!);

        pendingApproval.Should().NotBeNull();
        var result = await approvalService.RejectAsync(pendingApproval!, "do not send to group");

        result.Status.Should().Be(ChannelDeliveryApprovalDispatchStatus.Completed);
        result.Approval.Should().NotBeNull();
        result.Approval!.Status.Should().Be(ApprovalStatus.Rejected);
        result.Approval.DecisionNote.Should().Be("do not send to group");

        fakeTelegram.SendCalls.Should().BeEmpty();

        var resolvedInbox = await inbox.GetByIdAsync(evaluation.InboxItemId!);
        resolvedInbox.Should().NotBeNull();
        resolvedInbox!.Status.Should().Be(InboxItemStatus.Resolved);
        resolvedInbox.ResolvedAt.Should().NotBeNull();

        var updatedBinding = await bindings.GetByIdAsync(binding.Id);
        updatedBinding.Should().NotBeNull();
        updatedBinding!.LastOutboundAt.Should().BeNull();

        var entries = await audit.ListByBindingIdAsync(binding.Id, 20);
        entries.Select(item => item.EventType).Should().Contain("approval.rejected");
        entries.Select(item => item.EventType).Should().NotContain("delivery.sent");

        var rejectionEntry = entries.Single(item => item.EventType == "approval.rejected");
        rejectionEntry.MetadataJson.Should().NotBeNullOrWhiteSpace();
        var outcome = JsonSerializer.Deserialize<ChannelTurnOutcome>(rejectionEntry.MetadataJson!, JsonOptions);
        outcome.Should().NotBeNull();
        outcome!.Kind.Should().Be(ChannelTurnOutcomeKind.NoAction);
        outcome.ReasonCode.Should().Be("approval_rejected");
        outcome.ApprovalId.Should().Be(evaluation.ApprovalId);
    }

    private static ServiceProvider CreateServiceProvider(string workspaceRoot, FakeTelegramApiClient fakeTelegramApiClient)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        services.AddKodaClawJsonStore(workspaceRoot);
        services.AddKodaClawControlPlane();
        services.AddSingleton<ITelegramApiClient>(fakeTelegramApiClient);
        services.AddKodaClawChannelHub();
        return services.BuildServiceProvider();
    }

    private static ChannelAccount BuildAccount(
        ChannelConnectorKind connectorKind,
        string id,
        DateTimeOffset timestamp)
    {
        return new ChannelAccount(
            Id: id,
            ConnectorKind: connectorKind,
            DisplayName: connectorKind == ChannelConnectorKind.Telegram ? "Telegram" : "Webhook",
            State: ChannelAccountState.Connected,
            CreatedAt: timestamp,
            UpdatedAt: timestamp,
            ConfigurationJson: connectorKind == ChannelConnectorKind.GenericWebhook
                ? "{\"sharedSecret\":\"hook-secret\",\"defaultThreadType\":\"Group\"}"
                : null);
    }

    private static ThreadBinding BuildBinding(
        string id,
        ChannelConnectorKind connectorKind,
        ChannelThreadType threadType,
        DateTimeOffset timestamp)
    {
        var isDirectMessage = threadType == ChannelThreadType.DirectMessage;
        return new ThreadBinding(
            Id: id,
            ConnectorKind: connectorKind,
            AccountId: connectorKind == ChannelConnectorKind.Telegram ? "telegram-main" : "webhook-group",
            ExternalThreadId: isDirectMessage ? "10001" : "group-thread-001",
            ThreadType: threadType,
            SessionId: isDirectMessage ? $"channel-dm-{id}" : $"channel-group-{id}",
            SessionKind: isDirectMessage ? SessionKind.ChannelDirectMessage : SessionKind.ChannelGroup,
            ChannelIdentity: new ChannelIdentity(
                Id: isDirectMessage ? "user-001" : "group-001",
                Username: isDirectMessage ? "alice" : "ops_bridge",
                DisplayName: isDirectMessage ? "Alice" : "Ops Bridge"),
            PolicyId: isDirectMessage ? "policy-default-dm" : "policy-default-group",
            DeliveryRuleId: isDirectMessage ? "delivery-default-dm" : "delivery-default-group",
            CreatedAt: timestamp,
            UpdatedAt: timestamp,
            LastInboundAt: timestamp,
            LastMessagePreview: "latest inbound");
    }

    private static ChannelOutboundDraft BuildDraft(
        ThreadBinding binding,
        DeliveryMode deliveryMode,
        DateTimeOffset timestamp)
    {
        return new ChannelOutboundDraft(
            DraftId: $"draft-{binding.Id}",
            BindingId: binding.Id,
            ConnectorKind: binding.ConnectorKind,
            AccountId: binding.AccountId,
            ExternalThreadId: binding.ExternalThreadId,
            MessageText: binding.ThreadType == ChannelThreadType.DirectMessage
                ? "Thanks, we are on it."
                : "Please keep this as draft.",
            DeliveryMode: deliveryMode,
            CreatedAt: timestamp,
            SessionId: binding.SessionId,
            CorrelationId: $"corr-{binding.Id}");
    }

    private sealed class FakeTelegramApiClient : ITelegramApiClient
    {
        public List<SendCall> SendCalls { get; } = [];

        public Task<TelegramUser> GetMeAsync(string botToken, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TelegramUser
            {
                Id = 90001,
                IsBot = true,
                FirstName = "Koda",
                Username = "koda_bot",
            });
        }

        public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
            string botToken,
            long? offset,
            int timeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TelegramUpdate>>([]);
        }

        public Task<TelegramSendMessageResult> SendMessageAsync(
            string botToken,
            long chatId,
            string text,
            string? parseMode = null,
            CancellationToken cancellationToken = default)
        {
            SendCalls.Add(new SendCall(botToken, chatId, text));
            return Task.FromResult(new TelegramSendMessageResult
            {
                MessageId = 99001,
            });
        }

        public Task<TelegramSendMessageResult> SendPhotoAsync(
            string botToken,
            long chatId,
            Stream photo,
            string contentType,
            string? caption,
            CancellationToken cancellationToken = default)
        {
            SendCalls.Add(new SendCall(botToken, chatId, caption ?? string.Empty));
            return Task.FromResult(new TelegramSendMessageResult { MessageId = 99002 });
        }

        public Task<TelegramSendMessageResult> SendAudioAsync(
            string botToken,
            long chatId,
            Stream audio,
            string contentType,
            string? caption,
            CancellationToken cancellationToken = default)
        {
            SendCalls.Add(new SendCall(botToken, chatId, caption ?? string.Empty));
            return Task.FromResult(new TelegramSendMessageResult { MessageId = 99003 });
        }

        public Task<TelegramSendMessageResult> SendVideoAsync(
            string botToken,
            long chatId,
            Stream video,
            string contentType,
            string? caption,
            int? durationSeconds = null,
            CancellationToken cancellationToken = default)
        {
            SendCalls.Add(new SendCall(botToken, chatId, caption ?? string.Empty));
            return Task.FromResult(new TelegramSendMessageResult { MessageId = 99004 });
        }

        public Task<TelegramSendMessageResult> EditMessageTextAsync(
            string botToken,
            long chatId,
            long messageId,
            string text,
            string? parseMode = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TelegramSendMessageResult { MessageId = messageId });
        }
    }

    private sealed record SendCall(string Token, long ChatId, string Text);

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
