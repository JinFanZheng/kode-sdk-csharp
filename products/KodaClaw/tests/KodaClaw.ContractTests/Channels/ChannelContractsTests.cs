using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;
using Xunit;

namespace KodaClaw.ContractTests.Channels;

public sealed class ChannelContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Channel_thread_detail_should_json_round_trip()
    {
        var payload = new ChannelThreadDetail(
            Account: new ChannelAccount(
                Id: "telegram-main",
                ConnectorKind: ChannelConnectorKind.Telegram,
                DisplayName: "Telegram Bot",
                State: ChannelAccountState.Connected,
                CreatedAt: new DateTimeOffset(2026, 3, 19, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt: new DateTimeOffset(2026, 3, 19, 0, 5, 0, TimeSpan.Zero),
                ExternalAccountId: "bot_001",
                CredentialReference: "env:TELEGRAM_BOT_TOKEN"),
            Binding: new ThreadBinding(
                Id: "binding-001",
                ConnectorKind: ChannelConnectorKind.Telegram,
                AccountId: "telegram-main",
                ExternalThreadId: "chat-10001",
                ThreadType: ChannelThreadType.DirectMessage,
                SessionId: "session-channel-001",
                SessionKind: SessionKind.ChannelDirectMessage,
                ChannelIdentity: new ChannelIdentity(
                    Id: "user-10001",
                    Username: "alice",
                    DisplayName: "Alice"),
                PolicyId: "policy-dm-default",
                DeliveryRuleId: "delivery-draft",
                CreatedAt: new DateTimeOffset(2026, 3, 19, 0, 1, 0, TimeSpan.Zero),
                UpdatedAt: new DateTimeOffset(2026, 3, 19, 0, 6, 0, TimeSpan.Zero),
                LastInboundAt: new DateTimeOffset(2026, 3, 19, 0, 7, 0, TimeSpan.Zero),
                LastMessagePreview: "hello from telegram"),
            Policy: new ChannelPolicy(
                Id: "policy-dm-default",
                ThreadType: ChannelThreadType.DirectMessage,
                UpdatedAt: new DateTimeOffset(2026, 3, 19, 0, 2, 0, TimeSpan.Zero),
                LoadUserProfile: true),
            DeliveryRule: new DeliveryRule(
                Id: "delivery-draft",
                Mode: DeliveryMode.DraftApproval,
                UpdatedAt: new DateTimeOffset(2026, 3, 19, 0, 3, 0, TimeSpan.Zero),
                AllowProactiveSend: false),
            RecentAudit:
            [
                new ChannelAuditEntry(
                    Id: "audit-001",
                    BindingId: "binding-001",
                    ConnectorKind: ChannelConnectorKind.Telegram,
                    AccountId: "telegram-main",
                    ExternalThreadId: "chat-10001",
                    ThreadType: ChannelThreadType.DirectMessage,
                    EventType: "message.received",
                    CreatedAt: new DateTimeOffset(2026, 3, 19, 0, 7, 0, TimeSpan.Zero),
                    Summary: "Inbound message accepted.")
            ],
            Session: new SessionSummary(
                SessionId: "session-channel-001",
                SessionKind: SessionKind.ChannelDirectMessage,
                Status: new SessionStatusSummary(
                    IsActiveMainSession: false,
                    BreakpointState: null,
                    MessageCount: 8,
                    PendingApprovalCount: 1),
                CreatedAt: new DateTimeOffset(2026, 3, 19, 0, 1, 0, TimeSpan.Zero),
                LastEventAt: new DateTimeOffset(2026, 3, 19, 0, 7, 0, TimeSpan.Zero)),
            PendingApprovalId: "approval-001",
            HasPendingDraft: true,
            PolicyEvidence:
            [
                "pending_approval",
            ],
            LastTurnOutcome: new ChannelTurnOutcome(
                Kind: ChannelTurnOutcomeKind.DraftCreated,
                Summary: "Thanks, I drafted a reply for review.",
                OccurredAt: new DateTimeOffset(2026, 3, 19, 0, 8, 0, TimeSpan.Zero),
                ReplyText: "Thanks, I drafted a reply for review.",
                DeliveryMode: DeliveryMode.DraftApproval,
                ApprovalId: "approval-001",
                DraftId: "draft-001",
                SourceEventId: "event-001",
                ReasonCode: "draft_created",
                HasExplicitMention: true));

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ChannelThreadDetail>(json, JsonOptions);

        json.Should().Contain("\"binding\"");
        json.Should().Contain("\"deliveryRule\"");
        json.Should().Contain("\"recentAudit\"");
        roundTrip.Should().NotBeNull();
        roundTrip!.Binding.SessionKind.Should().Be(SessionKind.ChannelDirectMessage);
        roundTrip.DeliveryRule.Mode.Should().Be(DeliveryMode.DraftApproval);
        roundTrip.RecentAudit.Should().ContainSingle().Which.EventType.Should().Be("message.received");
        roundTrip.HasPendingDraft.Should().BeTrue();
        roundTrip.PolicyEvidence.Should().Equal("pending_approval");
        roundTrip.LastTurnOutcome.Should().NotBeNull();
        roundTrip.LastTurnOutcome!.Kind.Should().Be(ChannelTurnOutcomeKind.DraftCreated);
        roundTrip.LastTurnOutcome.ReasonCode.Should().Be("draft_created");
        roundTrip.LastTurnOutcome.HasExplicitMention.Should().BeTrue();
    }

    [Fact]
    public void Channel_event_envelope_should_json_round_trip()
    {
        var payload = new ChannelEventEnvelope(
            EventId: "event-001",
            EventType: ChannelEventType.MessageReceived,
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            AccountId: "webhook-default",
            ExternalThreadId: "hook-thread-001",
            ThreadType: ChannelThreadType.Group,
            OccurredAt: new DateTimeOffset(2026, 3, 19, 1, 0, 0, TimeSpan.Zero),
            Sender: new ChannelIdentity(
                Id: "sender-001",
                DisplayName: "Webhook Sender"),
            ExternalMessageId: "message-001",
            Text: "payload text",
            CorrelationId: "corr-001",
            MetadataJson: """{"eventName":"issue.opened"}""");

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ChannelEventEnvelope>(json, JsonOptions);

        json.Should().Contain("\"eventType\":\"MessageReceived\"");
        roundTrip.Should().NotBeNull();
        roundTrip!.ConnectorKind.Should().Be(ChannelConnectorKind.GenericWebhook);
        roundTrip.ThreadType.Should().Be(ChannelThreadType.Group);
        roundTrip.Sender.Should().NotBeNull();
        roundTrip.Sender!.DisplayName.Should().Be("Webhook Sender");
    }

    [Fact]
    public void Upsert_channel_account_request_should_json_round_trip()
    {
        var payload = new UpsertChannelAccountRequest(
            Id: "webhook-main",
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            DisplayName: "Generic Webhook",
            CredentialReference: "env:KODACLAW_WEBHOOK_SECRET",
            Description: "Loopback integration account",
            ConfigurationJson: """{"defaultThreadType":"Group"}""",
            InboundEnabled: true);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<UpsertChannelAccountRequest>(json, JsonOptions);

        json.Should().Contain("\"connectorKind\":\"GenericWebhook\"");
        roundTrip.Should().Be(payload);
    }

    [Fact]
    public void Channels_query_response_should_json_round_trip()
    {
        var payload = new ChannelsQueryResponse(
        [
            new ChannelThreadSummary(
                BindingId: "binding-001",
                ConnectorKind: ChannelConnectorKind.GenericWebhook,
                AccountId: "webhook-main",
                ExternalThreadId: "thread-001",
                ThreadType: ChannelThreadType.Group,
                SessionId: "channel-group-001",
                SessionKind: SessionKind.ChannelGroup,
                DisplayTitle: "Ops Bridge",
                DeliveryMode: DeliveryMode.RequireApproval,
                AccountState: ChannelAccountState.Connected,
                UpdatedAt: new DateTimeOffset(2026, 3, 19, 8, 0, 0, TimeSpan.Zero),
                LastInboundAt: new DateTimeOffset(2026, 3, 19, 8, 1, 0, TimeSpan.Zero),
                LastMessagePreview: "incident opened",
                PendingApprovalId: "approval-001",
                HasPendingDraft: true,
                LastTurnOutcome: new ChannelTurnOutcome(
                    Kind: ChannelTurnOutcomeKind.ApprovalRequested,
                    Summary: "Please approve the proposed response.",
                    OccurredAt: new DateTimeOffset(2026, 3, 19, 8, 2, 0, TimeSpan.Zero),
                    ReplyText: "Please confirm the rollout window.",
                    DeliveryMode: DeliveryMode.RequireApproval,
                    ApprovalId: "approval-001",
                    InboxItemId: "inbox-001",
                    DraftId: "draft-001",
                    SourceEventId: "event-001",
                    ReasonCode: "approval_requested",
                    HasExplicitMention: true)),
        ]);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ChannelsQueryResponse>(json, JsonOptions);

        json.Should().Contain("\"items\"");
        roundTrip.Should().NotBeNull();
        roundTrip!.Items.Should().ContainSingle();
        roundTrip.Items[0].DeliveryMode.Should().Be(DeliveryMode.RequireApproval);
        roundTrip.Items[0].PendingApprovalId.Should().Be("approval-001");
        roundTrip.Items[0].LastTurnOutcome.Should().NotBeNull();
        roundTrip.Items[0].LastTurnOutcome!.Kind.Should().Be(ChannelTurnOutcomeKind.ApprovalRequested);
        roundTrip.Items[0].LastTurnOutcome!.ReasonCode.Should().Be("approval_requested");
        roundTrip.Items[0].LastTurnOutcome!.HasExplicitMention.Should().BeTrue();
    }

    [Fact]
    public void Feishu_channel_account_should_json_round_trip()
    {
        var payload = new ChannelAccount(
            Id: "feishu-main",
            ConnectorKind: ChannelConnectorKind.Feishu,
            DisplayName: "飞书 Bot",
            State: ChannelAccountState.Connected,
            CreatedAt: new DateTimeOffset(2026, 3, 22, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 22, 0, 5, 0, TimeSpan.Zero),
            ExternalAccountId: "cli_abc123",
            CredentialReference: "env:FEISHU_APP_SECRET",
            ConfigurationJson: """{"appId":"cli_abc123","defaultDeliveryMode":"RequireApproval"}""",
            InboundEnabled: true);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ChannelAccount>(json, JsonOptions);

        json.Should().Contain("\"connectorKind\":\"Feishu\"");
        json.Should().Contain("\"state\":\"Connected\"");
        roundTrip.Should().NotBeNull();
        roundTrip!.ConnectorKind.Should().Be(ChannelConnectorKind.Feishu);
        roundTrip.State.Should().Be(ChannelAccountState.Connected);
        roundTrip.ExternalAccountId.Should().Be("cli_abc123");
        roundTrip.CredentialReference.Should().Be("env:FEISHU_APP_SECRET");
    }

    [Fact]
    public void Feishu_upsert_request_should_json_round_trip()
    {
        var payload = new UpsertChannelAccountRequest(
            Id: "feishu-main",
            ConnectorKind: ChannelConnectorKind.Feishu,
            DisplayName: "飞书 Bot",
            ConfigurationJson: """{"appId":"cli_abc","credentialReference":"env:FEISHU_APP_SECRET"}""",
            InboundEnabled: true);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<UpsertChannelAccountRequest>(json, JsonOptions);

        json.Should().Contain("\"connectorKind\":\"Feishu\"");
        roundTrip.Should().NotBeNull();
        roundTrip!.ConnectorKind.Should().Be(ChannelConnectorKind.Feishu);
        roundTrip.Id.Should().Be("feishu-main");
    }

    [Fact]
    public void TestFeishuCredentials_response_should_json_round_trip()
    {
        var ok = new TestFeishuCredentialsResponse(Ok: true, AppName: "MyFeishuBot");
        var err = new TestFeishuCredentialsResponse(Ok: false, Error: "invalid app credentials");

        var okJson = JsonSerializer.Serialize(ok, JsonOptions);
        var errJson = JsonSerializer.Serialize(err, JsonOptions);
        var okRound = JsonSerializer.Deserialize<TestFeishuCredentialsResponse>(okJson, JsonOptions);
        var errRound = JsonSerializer.Deserialize<TestFeishuCredentialsResponse>(errJson, JsonOptions);

        okJson.Should().Contain("\"ok\":true");
        okJson.Should().Contain("\"appName\":\"MyFeishuBot\"");
        okRound.Should().NotBeNull();
        okRound!.Ok.Should().BeTrue();
        okRound.AppName.Should().Be("MyFeishuBot");

        errJson.Should().Contain("\"ok\":false");
        errRound.Should().NotBeNull();
        errRound!.Ok.Should().BeFalse();
        errRound.Error.Should().Be("invalid app credentials");
    }
}
