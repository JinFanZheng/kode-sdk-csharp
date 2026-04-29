using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.Webhook;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Secrets;
using Xunit;

namespace KodaClaw.IntegrationTests.ChannelHub;

public sealed class GenericWebhookConnectorIntegrationTests
{
    [Fact]
    public async Task Handle_inbound_should_normalize_group_message_received_and_dispatch_event()
    {
        var connector = new GenericWebhookConnector();
        var account = BuildAccount(
            accountId: "webhook-group",
            configurationJson: """
                {
                  "sharedSecret": "hook-secret",
                  "defaultThreadType": "Group"
                }
                """);
        var captured = new List<ChannelEventEnvelope>();
        await connector.StartAsync(account, (evt, _) =>
        {
            captured.Add(evt);
            return Task.CompletedTask;
        });

        var payload = """
            {
              "eventType": "message.received",
              "eventId": "event-001",
              "externalThreadId": "group-thread-001",
              "threadType": "group",
              "occurredAt": "2026-03-19T07:00:00Z",
              "messageId": "msg-001",
              "text": "hello from webhook",
              "sender": {
                "id": "sender-001",
                "username": "alpha",
                "displayName": "Alpha"
              }
            }
            """;

        var result = await connector.HandleInboundAsync(
            accountId: account.Id,
            payloadJson: payload,
            presentedSharedSecret: "hook-secret");

        result.Accepted.Should().BeTrue();
        result.RejectionCode.Should().BeNull();
        result.Event.Should().NotBeNull();
        result.Event!.EventType.Should().Be(ChannelEventType.MessageReceived);
        result.Event.ThreadType.Should().Be(ChannelThreadType.Group);
        result.Event.ExternalThreadId.Should().Be("group-thread-001");
        result.Event.Text.Should().Be("hello from webhook");
        result.Event.ExternalMessageId.Should().Be("msg-001");
        result.Event.Sender.Should().NotBeNull();
        result.Event.Sender!.Id.Should().Be("sender-001");

        captured.Should().ContainSingle();
        captured[0].EventId.Should().Be("event-001");
        captured[0].ConnectorKind.Should().Be(ChannelConnectorKind.GenericWebhook);
        captured[0].MetadataJson.Should().Contain("message.received");
    }

    [Fact]
    public async Task Handle_inbound_should_use_credential_reference_and_default_to_direct_message()
    {
        const string environmentKey = "KODACLAW_WEBHOOK_KC0506_SECRET";
        Environment.SetEnvironmentVariable(environmentKey, "env-secret");

        try
        {
            var connector = new GenericWebhookConnector();
            var account = BuildAccount(
                accountId: "webhook-dm",
                configurationJson: """
                    {
                      "defaultThreadType": "DirectMessage"
                    }
                    """,
                credentialReference: $"env:{environmentKey}");
            var captured = new List<ChannelEventEnvelope>();

            var payload = """
                {
                  "event": "message.received",
                  "externalThreadId": "dm-thread-001",
                  "text": "dm payload"
                }
                """;

            var result = await connector.HandleInboundAsync(
                account: account,
                payloadJson: payload,
                presentedSharedSecret: "env-secret",
                onEvent: (evt, _) =>
                {
                    captured.Add(evt);
                    return Task.CompletedTask;
                });

            result.Accepted.Should().BeTrue();
            result.Event.Should().NotBeNull();
            result.Event!.ThreadType.Should().Be(ChannelThreadType.DirectMessage);
            captured.Should().ContainSingle();
            captured[0].ThreadType.Should().Be(ChannelThreadType.DirectMessage);
            captured[0].EventType.Should().Be(ChannelEventType.MessageReceived);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentKey, null);
        }
    }

    [Fact]
    public async Task Handle_inbound_should_reject_when_shared_secret_does_not_match()
    {
        var connector = new GenericWebhookConnector();
        var account = BuildAccount(
            accountId: "webhook-secret-mismatch",
            configurationJson: """
                {
                  "sharedSecret": "expected-secret"
                }
                """);
        var captured = new List<ChannelEventEnvelope>();
        await connector.StartAsync(account, (evt, _) =>
        {
            captured.Add(evt);
            return Task.CompletedTask;
        });

        var payload = """
            {
              "eventType": "message.received",
              "externalThreadId": "group-thread-002",
              "threadType": "group",
              "text": "this should be rejected"
            }
            """;

        var result = await connector.HandleInboundAsync(
            accountId: account.Id,
            payloadJson: payload,
            presentedSharedSecret: "wrong-secret");

        result.Accepted.Should().BeFalse();
        result.RejectionCode.Should().Be("secret_mismatch");
        result.Event.Should().BeNull();
        captured.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_inbound_should_resolve_secret_ref_credential_reference_from_secret_store()
    {
        var secretRef = new SecretRef("memory", "channels", "webhook-secret");
        var connector = new GenericWebhookConnector(new FakeSecretStore(new Dictionary<string, string?>
        {
            [secretRef.ToReferenceString()] = "memory-webhook-secret"
        }));
        var account = BuildAccount(
            accountId: "webhook-secret-ref",
            configurationJson: """
                {
                  "defaultThreadType": "Group"
                }
                """,
            credentialReference: secretRef.ToReferenceString());
        var captured = new List<ChannelEventEnvelope>();

        var payload = """
            {
              "eventType": "message.received",
              "externalThreadId": "group-thread-003",
              "threadType": "group",
              "text": "payload from secret ref"
            }
            """;

        var result = await connector.HandleInboundAsync(
            account: account,
            payloadJson: payload,
            presentedSharedSecret: "memory-webhook-secret",
            onEvent: (evt, _) =>
            {
                captured.Add(evt);
                return Task.CompletedTask;
            });

        result.Accepted.Should().BeTrue();
        captured.Should().ContainSingle();
        captured[0].Text.Should().Be("payload from secret ref");
    }

    private static ChannelAccount BuildAccount(
        string accountId,
        string? configurationJson,
        string? credentialReference = null)
    {
        var now = new DateTimeOffset(2026, 3, 19, 7, 0, 0, TimeSpan.Zero);
        return new ChannelAccount(
            Id: accountId,
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            DisplayName: "Generic Webhook",
            State: ChannelAccountState.Connected,
            CreatedAt: now,
            UpdatedAt: now,
            CredentialReference: credentialReference,
            ConfigurationJson: configurationJson);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly IReadOnlyDictionary<string, string?> _values;

        public FakeSecretStore(IReadOnlyDictionary<string, string?> values)
        {
            _values = values;
        }

        public Task<string?> GetAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        {
            _values.TryGetValue(secretRef.ToReferenceString(), out var value);
            return Task.FromResult(value);
        }

        public Task UpsertAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SecretDescriptor> DescribeAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
