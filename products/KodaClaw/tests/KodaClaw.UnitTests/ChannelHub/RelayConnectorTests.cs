using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.Relay;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

public sealed class RelayConnectorTests
{
    private static readonly RelayConnectorConfiguration DefaultConfiguration = new(
        AccountId: "relay-account",
        RelayUrl: "wss://relay.example/ws",
        SharedSecret: null,
        DefaultThreadType: ChannelThreadType.DirectMessage,
        DefaultDeliveryMode: DeliveryMode.AutoSend,
        NotifyChannelId: "channel-001");

    [Fact]
    public async Task DispatchEventAsync_should_forward_strict_message_event_to_onEvent()
    {
        var diagnostics = new List<DiagnosticEvent>();
        var diagnosticsService = BuildDiagnosticsService(diagnostics);
        var connector = new RelayConnector(
            logger: NullLogger<RelayConnector>.Instance,
            diagnosticsService: diagnosticsService.Object);
        var captured = new List<ChannelEventEnvelope>();

        using var document = JsonDocument.Parse(
            """
            {
              "eventId": "relay_evt_20260407_0101",
              "eventType": "MessageReceived",
              "threadType": "DirectMessage",
              "externalThreadId": "crypto-monitor:ETH:1h",
              "externalMessageId": "eth-1h-2026-04-07T03:00:00Z",
              "text": "ETH 1h breaks threshold, analyze impact.",
              "sender": {
                "id": "crypto-monitor",
                "displayName": "Crypto Monitor",
                "isBot": true
              },
              "occurredAt": "2026-04-07T03:00:00Z",
              "correlationId": "monitor-run-201"
            }
            """);

        await InvokeDispatchAsync(
            connector,
            accountId: "account-1",
            configuration: DefaultConfiguration,
            eventJson: document.RootElement,
            onEvent: (envelope, _) =>
            {
                captured.Add(envelope);
                return Task.CompletedTask;
            });

        captured.Should().ContainSingle();
        captured[0].EventType.Should().Be(ChannelEventType.MessageReceived);
        captured[0].ExternalThreadId.Should().Be("crypto-monitor:ETH:1h");
        captured[0].ExternalMessageId.Should().Be("eth-1h-2026-04-07T03:00:00Z");
        captured[0].Text.Should().Be("ETH 1h breaks threshold, analyze impact.");
        diagnostics.Should().ContainSingle(e => e.EventType == "relay.event_dispatched");
        diagnostics.Should().Contain(e =>
            e.EventType == "relay.event_dispatched" &&
            e.Attributes!["eventId"] == "relay_evt_20260407_0101" &&
            e.Attributes["externalThreadId"] == "crypto-monitor:ETH:1h");
    }

    [Fact]
    public async Task DispatchEventAsync_should_store_notification_in_inbox_without_dispatching_turn()
    {
        InboxItem? capturedInboxItem = null;
        var diagnostics = new List<DiagnosticEvent>();
        var diagnosticsService = BuildDiagnosticsService(diagnostics);
        var inboxRepository = new Mock<IInboxRepository>();
        inboxRepository
            .Setup(repo => repo.UpsertAsync(It.IsAny<InboxItem>(), It.IsAny<CancellationToken>()))
            .Callback<InboxItem, CancellationToken>((item, _) => capturedInboxItem = item)
            .Returns(Task.CompletedTask);

        var connector = new RelayConnector(
            logger: NullLogger<RelayConnector>.Instance,
            diagnosticsService: diagnosticsService.Object,
            inboxRepository: inboxRepository.Object);

        using var document = JsonDocument.Parse(
            """
            {
              "eventId": "relay_evt_20260407_0102",
              "eventType": "NotificationReceived",
              "threadType": "DirectMessage",
              "externalThreadId": "system-alert:prod-api",
              "externalMessageId": "alert-2026-04-07T03:05:00Z",
              "text": "Prod API latency is elevated.",
              "sender": {
                "id": "system-alert",
                "displayName": "System Alert",
                "isBot": true
              },
              "occurredAt": "2026-04-07T03:05:00Z"
            }
            """);

        var dispatched = false;
        await InvokeDispatchAsync(
            connector,
            accountId: "account-1",
            configuration: DefaultConfiguration,
            eventJson: document.RootElement,
            onEvent: (_, _) =>
            {
                dispatched = true;
                return Task.CompletedTask;
            });

        dispatched.Should().BeFalse();
        capturedInboxItem.Should().NotBeNull();
        capturedInboxItem!.Id.Should().Be("relay-notif-relay_evt_20260407_0102");
        capturedInboxItem.Kind.Should().Be(InboxItemKind.Information);
        capturedInboxItem.Status.Should().Be(InboxItemStatus.Open);
        capturedInboxItem.Title.Should().Be("Prod API latency is elevated.");
        capturedInboxItem.PayloadJson.Should().Contain("NotificationReceived");
        diagnostics.Should().Contain(e =>
            e.EventType == "relay.notification_received" &&
            e.Attributes!["eventId"] == "relay_evt_20260407_0102");
        diagnostics.Should().Contain(e => e.EventType == "relay.notification_handled");
    }

    [Fact]
    public async Task DispatchEventAsync_should_drop_invalid_message_event_without_dispatch()
    {
        var diagnostics = new List<DiagnosticEvent>();
        var diagnosticsService = BuildDiagnosticsService(diagnostics);
        var connector = new RelayConnector(
            logger: NullLogger<RelayConnector>.Instance,
            diagnosticsService: diagnosticsService.Object);

        using var document = JsonDocument.Parse(
            """
            {
              "eventId": "relay_evt_20260407_0103",
              "eventType": "MessageReceived",
              "threadType": "DirectMessage",
              "externalThreadId": "crypto-monitor:SOL:15m",
              "externalMessageId": "sol-15m-2026-04-07T03:10:00Z",
              "sender": {
                "id": "crypto-monitor",
                "displayName": "Crypto Monitor",
                "isBot": true
              },
              "occurredAt": "2026-04-07T03:10:00Z"
            }
            """);

        var dispatched = false;
        await InvokeDispatchAsync(
            connector,
            accountId: "account-1",
            configuration: DefaultConfiguration,
            eventJson: document.RootElement,
            onEvent: (_, _) =>
            {
                dispatched = true;
                return Task.CompletedTask;
            });

        dispatched.Should().BeFalse();
        diagnostics.Should().Contain(e =>
            e.EventType == "relay.event_rejected" &&
            e.Attributes!["eventId"] == "relay_evt_20260407_0103" &&
            e.Attributes["error"] == "Missing required EventFrame field 'text'.");
    }

    private static Mock<IDiagnosticsService> BuildDiagnosticsService(List<DiagnosticEvent> diagnostics)
    {
        var diagnosticsService = new Mock<IDiagnosticsService>();
        diagnosticsService
            .Setup(service => service.Record(It.IsAny<DiagnosticEvent>()))
            .Callback<DiagnosticEvent>(diagnostics.Add);
        return diagnosticsService;
    }

    private static async Task InvokeDispatchAsync(
        RelayConnector connector,
        string accountId,
        RelayConnectorConfiguration configuration,
        JsonElement eventJson,
        Func<ChannelEventEnvelope, CancellationToken, Task> onEvent)
    {
        var method = typeof(RelayConnector).GetMethod(
            "DispatchEventAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var task = (Task?)method!.Invoke(connector, [accountId, configuration, eventJson, onEvent, CancellationToken.None]);
        task.Should().NotBeNull();
        await task!;
    }
}
