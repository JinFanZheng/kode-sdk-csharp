using System.Collections.Concurrent;
using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.Telegram;
using KodaClaw.Contracts;
using Xunit;

namespace KodaClaw.IntegrationTests.ChannelHub;

public sealed class TelegramConnectorIntegrationTests
{
    [Fact]
    public async Task Start_should_long_poll_and_normalize_dm_and_group_edited_events()
    {
        var fakeApiClient = new FakeTelegramApiClient(
            [
                [
                    new TelegramUpdate
                    {
                        UpdateId = 1001,
                        Message = new TelegramMessage
                        {
                            MessageId = 11,
                            DateUnixSeconds = 1_773_904_800,
                            Text = "hello dm",
                            Chat = new TelegramChat
                            {
                                Id = 10001,
                                Type = "private",
                                Username = "alice",
                            },
                            From = new TelegramUser
                            {
                                Id = 20001,
                                FirstName = "Alice",
                                Username = "alice",
                            },
                        },
                    },
                    new TelegramUpdate
                    {
                        UpdateId = 1002,
                        EditedMessage = new TelegramMessage
                        {
                            MessageId = 21,
                            DateUnixSeconds = 1_773_904_860,
                            Text = "edited group",
                            Chat = new TelegramChat
                            {
                                Id = -100200,
                                Type = "supergroup",
                                Title = "Ops Bridge",
                            },
                            From = new TelegramUser
                            {
                                Id = 30001,
                                FirstName = "Ops",
                                LastName = "Bot",
                                IsBot = true,
                            },
                        },
                    },
                ],
            ]);

        var connector = new TelegramConnector(
            fakeApiClient,
            new TelegramConnectorOptions(
                IdleDelay: TimeSpan.FromMilliseconds(10),
                ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                IgnoreNonTextMessages: true));

        var account = BuildAccount(
            accountId: "telegram-main",
            credentialReference: "inline:telegram-token-inline");
        var captured = new ConcurrentQueue<ChannelEventEnvelope>();

        await connector.StartAsync(
            account,
            (evt, _) =>
            {
                captured.Enqueue(evt);
                return Task.CompletedTask;
            });

        await WaitUntilAsync(
            () => captured.Count >= 2,
            timeout: TimeSpan.FromSeconds(3));
        await connector.StopAsync(account.Id);

        captured.Should().HaveCount(2);
        var events = captured.ToArray();

        events[0].EventType.Should().Be(ChannelEventType.MessageReceived);
        events[0].ThreadType.Should().Be(ChannelThreadType.DirectMessage);
        events[0].ExternalThreadId.Should().Be("10001");
        events[0].Text.Should().Be("hello dm");
        events[0].Sender!.DisplayName.Should().Be("Alice");

        events[1].EventType.Should().Be(ChannelEventType.MessageEdited);
        events[1].ThreadType.Should().Be(ChannelThreadType.Group);
        events[1].ExternalThreadId.Should().Be("-100200");
        events[1].Text.Should().Be("edited group");
        events[1].Recipient!.DisplayName.Should().Be("Ops Bridge");

        fakeApiClient.GetMeTokens.Should().ContainSingle().Which.Should().Be("telegram-token-inline");
        fakeApiClient.GetUpdatesTokens.Should().NotBeEmpty();
        fakeApiClient.GetUpdatesTokens.Should().OnlyContain(token => token == "telegram-token-inline");
    }

    [Fact]
    public async Task Send_should_call_send_message_with_numeric_external_thread_id()
    {
        var fakeApiClient = new FakeTelegramApiClient();
        var connector = new TelegramConnector(
            fakeApiClient,
            new TelegramConnectorOptions(
                IdleDelay: TimeSpan.FromMilliseconds(10),
                ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                IgnoreNonTextMessages: true));
        var account = BuildAccount(
            accountId: "telegram-send",
            configurationJson: """{"botToken":"inline-send-token"}""");

        await connector.StartAsync(account, (_, _) => Task.CompletedTask);

        await connector.SendAsync(new ChannelOutboundDraft(
            DraftId: "draft-001",
            BindingId: "binding-001",
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: account.Id,
            ExternalThreadId: "-100778899",
            MessageText: "outbound hello",
            DeliveryMode: DeliveryMode.AutoSend,
            CreatedAt: DateTimeOffset.UtcNow));

        await connector.StopAsync(account.Id);

        fakeApiClient.SendCalls.Should().ContainSingle();
        fakeApiClient.SendCalls[0].Token.Should().Be("inline-send-token");
        fakeApiClient.SendCalls[0].ChatId.Should().Be(-100778899);
        fakeApiClient.SendCalls[0].Text.Should().Be("outbound hello");
    }

    [Fact]
    public async Task Start_should_resolve_env_credential_reference()
    {
        const string environmentKey = "KODACLAW_TELEGRAM_TOKEN_KC0505";
        Environment.SetEnvironmentVariable(environmentKey, "env-token-0505");

        try
        {
            var fakeApiClient = new FakeTelegramApiClient();
            var connector = new TelegramConnector(
                fakeApiClient,
                new TelegramConnectorOptions(
                    IdleDelay: TimeSpan.FromMilliseconds(10),
                    ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                    IgnoreNonTextMessages: true));
            var account = BuildAccount(
                accountId: "telegram-env",
                credentialReference: $"env:{environmentKey}");

            await connector.StartAsync(account, (_, _) => Task.CompletedTask);
            await connector.StopAsync(account.Id);

            fakeApiClient.GetMeTokens.Should().ContainSingle().Which.Should().Be("env-token-0505");
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentKey, null);
        }
    }

    [Fact]
    public async Task Start_should_resolve_secret_ref_credential_reference_from_secret_store()
    {
        var secretRef = new SecretRef("memory", "channels", "telegram-main");
        var fakeApiClient = new FakeTelegramApiClient();
        var connector = new TelegramConnector(
            fakeApiClient,
            new TelegramConnectorOptions(
                IdleDelay: TimeSpan.FromMilliseconds(10),
                ErrorRetryDelay: TimeSpan.FromMilliseconds(10),
                IgnoreNonTextMessages: true),
            new FakeSecretStore(new Dictionary<string, string?>
            {
                [secretRef.ToReferenceString()] = "memory-secret-token"
            }));
        var account = BuildAccount(
            accountId: "telegram-memory",
            credentialReference: secretRef.ToReferenceString());

        await connector.StartAsync(account, (_, _) => Task.CompletedTask);
        await connector.StopAsync(account.Id);

        fakeApiClient.GetMeTokens.Should().ContainSingle().Which.Should().Be("memory-secret-token");
    }

    private static ChannelAccount BuildAccount(
        string accountId,
        string? credentialReference = null,
        string? configurationJson = null)
    {
        var now = new DateTimeOffset(2026, 3, 19, 12, 0, 0, TimeSpan.Zero);
        return new ChannelAccount(
            Id: accountId,
            ConnectorKind: ChannelConnectorKind.Telegram,
            DisplayName: "Telegram Bot",
            State: ChannelAccountState.Connected,
            CreatedAt: now,
            UpdatedAt: now,
            CredentialReference: credentialReference,
            ConfigurationJson: configurationJson);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow - start > timeout)
            {
                throw new TimeoutException("Condition was not met within timeout.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class FakeTelegramApiClient : ITelegramApiClient
    {
        private readonly ConcurrentQueue<IReadOnlyList<TelegramUpdate>> _updatesBatches = new();

        public FakeTelegramApiClient(IEnumerable<IReadOnlyList<TelegramUpdate>>? updatesBatches = null)
        {
            if (updatesBatches is null)
            {
                return;
            }

            foreach (var batch in updatesBatches)
            {
                _updatesBatches.Enqueue(batch);
            }
        }

        public List<string> GetMeTokens { get; } = [];

        public List<string> GetUpdatesTokens { get; } = [];

        public List<SendCall> SendCalls { get; } = [];

        public Task<TelegramUser> GetMeAsync(string botToken, CancellationToken cancellationToken = default)
        {
            GetMeTokens.Add(botToken);
            return Task.FromResult(new TelegramUser
            {
                Id = 90001,
                IsBot = true,
                FirstName = "Koda",
                Username = "koda_bot",
            });
        }

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
            string botToken,
            long? offset,
            int timeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            GetUpdatesTokens.Add(botToken);

            if (_updatesBatches.TryDequeue(out var batch))
            {
                return batch;
            }

            await Task.Delay(10, cancellationToken);
            return [];
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
