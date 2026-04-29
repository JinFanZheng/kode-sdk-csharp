using System.Text.Json;
using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.Relay;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

public sealed class RelayEventFrameParserTests
{
    private static readonly RelayConnectorConfiguration DefaultConfiguration = new(
        AccountId: "relay-account",
        RelayUrl: "wss://relay.example/ws",
        SharedSecret: null,
        DefaultThreadType: ChannelThreadType.DirectMessage,
        DefaultDeliveryMode: DeliveryMode.AutoSend);

    [Fact]
    public void TryParse_should_map_strict_message_event_frame()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "eventId": "relay_evt_20260407_0001",
              "eventType": "MessageReceived",
              "threadType": "DirectMessage",
              "externalThreadId": "crypto-monitor:BTC:4h",
              "externalMessageId": "btc-4h-2026-04-07T02:00:00Z",
              "text": "BTC 4h down 1.5%, analyze next action.",
              "sender": {
                "id": "crypto-monitor",
                "displayName": "Crypto Monitor",
                "isBot": true,
                "metadata": {
                  "sourceType": "system"
                }
              },
              "recipient": {
                "id": "koda",
                "displayName": "KodaClaw",
                "isBot": true
              },
              "occurredAt": "2026-04-07T02:00:00Z",
              "correlationId": "monitor-run-123",
              "metadataJson": "{\"symbol\":\"BTC\"}"
            }
            """);

        var parsed = RelayEventFrameParser.TryParse(
            accountId: "account-1",
            configuration: DefaultConfiguration,
            root: document.RootElement,
            envelope: out var envelope,
            error: out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        envelope.Should().NotBeNull();
        envelope!.EventId.Should().Be("relay_evt_20260407_0001");
        envelope.EventType.Should().Be(ChannelEventType.MessageReceived);
        envelope.ThreadType.Should().Be(ChannelThreadType.DirectMessage);
        envelope.ExternalThreadId.Should().Be("crypto-monitor:BTC:4h");
        envelope.ExternalMessageId.Should().Be("btc-4h-2026-04-07T02:00:00Z");
        envelope.Text.Should().Be("BTC 4h down 1.5%, analyze next action.");
        envelope.Sender.Should().NotBeNull();
        envelope.Sender!.Id.Should().Be("crypto-monitor");
        envelope.Sender.DisplayName.Should().Be("Crypto Monitor");
        envelope.Sender.IsBot.Should().BeTrue();
        using (var metadataDocument = JsonDocument.Parse(envelope.Sender.MetadataJson!))
        {
            metadataDocument.RootElement.GetProperty("sourceType").GetString().Should().Be("system");
        }
        envelope.Recipient.Should().BeEquivalentTo(new ChannelIdentity(
            Id: "koda",
            DisplayName: "KodaClaw",
            IsBot: true));
        envelope.CorrelationId.Should().Be("monitor-run-123");
        envelope.MetadataJson.Should().Be("{\"symbol\":\"BTC\"}");
        envelope.DefaultDeliveryMode.Should().Be(DeliveryMode.AutoSend);
    }

    [Fact]
    public void TryParse_should_reject_message_frame_without_text()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "eventId": "relay_evt_20260407_0002",
              "eventType": "MessageReceived",
              "threadType": "DirectMessage",
              "externalThreadId": "crypto-monitor:BTC:4h",
              "externalMessageId": "btc-4h-2026-04-07T02:05:00Z",
              "sender": {
                "id": "crypto-monitor",
                "displayName": "Crypto Monitor",
                "isBot": true
              },
              "occurredAt": "2026-04-07T02:05:00Z"
            }
            """);

        var parsed = RelayEventFrameParser.TryParse(
            accountId: "account-1",
            configuration: DefaultConfiguration,
            root: document.RootElement,
            envelope: out var envelope,
            error: out var error);

        parsed.Should().BeFalse();
        envelope.Should().BeNull();
        error.Should().Be("Missing required EventFrame field 'text'.");
    }

    [Fact]
    public void TryParse_should_reject_unsupported_event_type()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "eventId": "relay_evt_20260407_0003",
              "eventType": "community_notification",
              "threadType": "DirectMessage",
              "externalThreadId": "crypto-monitor:BTC:4h",
              "externalMessageId": "btc-4h-2026-04-07T02:10:00Z",
              "text": "Legacy notification payload",
              "sender": {
                "id": "crypto-monitor",
                "displayName": "Crypto Monitor",
                "isBot": true
              },
              "occurredAt": "2026-04-07T02:10:00Z"
            }
            """);

        var parsed = RelayEventFrameParser.TryParse(
            accountId: "account-1",
            configuration: DefaultConfiguration,
            root: document.RootElement,
            envelope: out var envelope,
            error: out var error);

        parsed.Should().BeFalse();
        envelope.Should().BeNull();
        error.Should().Be("Unsupported Relay eventType 'community_notification'.");
    }

    [Theory]
    [InlineData("NotificationReceived", true)]
    [InlineData("community_notification", false)]
    [InlineData("MessageReceived", false)]
    [InlineData(null, false)]
    public void IsNotificationEventType_should_only_accept_strict_notification_value(string? eventType, bool expected)
    {
        RelayEventFrameParser.IsNotificationEventType(eventType).Should().Be(expected);
    }
}
