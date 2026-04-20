using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.Telegram;
using KodaClaw.Contracts;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class ChannelApiIntegrationTests
{
    private const string GatewayToken = "test-token";

    [Fact]
    public async Task Channel_accounts_should_require_token()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);

        var response = await hosted.Client.GetAsync("/api/channels/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Generic_webhook_flow_should_create_thread_binding_and_audit()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var accountRequest = new UpsertChannelAccountRequest(
            Id: "webhook-main",
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            DisplayName: "Generic Webhook",
            ConfigurationJson: """
                {
                  "sharedSecret": "hook-secret",
                  "defaultThreadType": "Group"
                }
                """);

        var createAccountResponse = await hosted.Client.PostAsJsonAsync("/api/channels/accounts", accountRequest);
        createAccountResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var webhookRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/channels/webhook/webhook-main/events")
        {
            Content = JsonContent.Create(new
            {
                eventType = "message.received",
                eventId = "event-001",
                externalThreadId = "group-thread-001",
                threadType = "group",
                occurredAt = "2026-03-19T09:00:00Z",
                messageId = "message-001",
                text = "incident opened",
                sender = new
                {
                    id = "sender-001",
                    displayName = "Ops Bridge",
                },
            }),
        };
        webhookRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
        webhookRequest.Headers.Add("X-KodaClaw-Webhook-Secret", "hook-secret");

        var webhookResponse = await hosted.Client.SendAsync(webhookRequest);
        webhookResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await webhookResponse.Content.ReadFromJsonAsync<ChannelThreadDetail>();
        detail.Should().NotBeNull();
        detail!.Account.Id.Should().Be("webhook-main");
        detail.Binding.ThreadType.Should().Be(ChannelThreadType.Group);
        detail.Binding.SessionKind.Should().Be(SessionKind.ChannelGroup);
        detail.DeliveryRule.Mode.Should().Be(DeliveryMode.RequireApproval);
        detail.RecentAudit.Should().ContainSingle();
        detail.RecentAudit[0].EventType.Should().Be("message.received");

        var accountsResponse = await hosted.Client.GetAsync("/api/channels/accounts?connectorKind=GenericWebhook");
        accountsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var accounts = await accountsResponse.Content.ReadFromJsonAsync<List<ChannelAccount>>();
        accounts.Should().NotBeNull();
        accounts!.Should().ContainSingle(item => item.Id == "webhook-main");

        var threadsResponse = await hosted.Client.GetAsync("/api/channels/threads?connectorKind=GenericWebhook");
        threadsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var threads = await threadsResponse.Content.ReadFromJsonAsync<ChannelsQueryResponse>();
        threads.Should().NotBeNull();
        threads!.Items.Should().ContainSingle();
        threads.Items[0].BindingId.Should().Be(detail.Binding.Id);
        threads.Items[0].DisplayTitle.Should().Be("Ops Bridge");
        threads.Items[0].DeliveryMode.Should().Be(DeliveryMode.RequireApproval);

        var auditResponse = await hosted.Client.GetAsync($"/api/channels/threads/{detail.Binding.Id}/audit?limit=10");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var auditItems = await auditResponse.Content.ReadFromJsonAsync<List<ChannelAuditEntry>>();
        auditItems.Should().NotBeNull();
        auditItems!.Should().ContainSingle();
        auditItems![0].BindingId.Should().Be(detail.Binding.Id);
        auditItems[0].EventType.Should().Be("message.received");
        auditItems[0].Summary.Should().Be("incident opened");
    }

    [Fact]
    public async Task Generic_webhook_rejection_should_surface_diagnostics_with_same_correlation_id()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var accountRequest = new UpsertChannelAccountRequest(
            Id: "webhook-reject",
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            DisplayName: "Rejecting Webhook",
            ConfigurationJson: """{"sharedSecret":"expected-secret"}""");
        var createAccountResponse = await hosted.Client.PostAsJsonAsync("/api/channels/accounts", accountRequest);
        createAccountResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var webhookRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/channels/webhook/webhook-reject/events")
        {
            Content = JsonContent.Create(new
            {
                eventType = "message.received",
                externalThreadId = "thread-reject-001",
                text = "should reject",
            }),
        };
        webhookRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
        webhookRequest.Headers.Add("X-KodaClaw-Webhook-Secret", "wrong-secret");
        webhookRequest.Headers.Add("X-KodaClaw-Correlation-Id", "corr-channel-reject-001");

        var webhookResponse = await hosted.Client.SendAsync(webhookRequest);
        webhookResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var error = await webhookResponse.Content.ReadFromJsonAsync<ErrorResponse>();
        error.Should().NotBeNull();
        error!.Code.Should().Be("channel.webhook_secret_mismatch");

        var diagnosticsResponse = await hosted.Client.GetAsync(
            "/api/diagnostics/recent?correlationId=corr-channel-reject-001");
        diagnosticsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var diagnostics = await diagnosticsResponse.Content.ReadFromJsonAsync<DiagnosticsQueryResponse>();
        diagnostics.Should().NotBeNull();
        diagnostics!.Events.Should().Contain(item =>
            item.EventType == "gateway.channels.webhook_rejected" &&
            item.CorrelationId == "corr-channel-reject-001");
    }

    [Fact]
    public async Task Generic_webhook_with_runtime_should_deliver_dm_turn_in_full_agent_mode()
    {
        using var workspace = new TempWorkspaceRoot();
        var modelProvider = new StubChannelTurnModelProvider("I will use channel_send to reply for you.");
        await using var hosted = await StartGatewayAsync(workspace.Path, modelProvider, enableRuntime: true);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var accountRequest = new UpsertChannelAccountRequest(
            Id: "webhook-dm-runtime",
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            DisplayName: "Runtime DM Webhook",
            ConfigurationJson: """{"sharedSecret":"hook-secret","defaultThreadType":"DirectMessage","defaultDeliveryMode":"DraftApproval"}""");
        var createAccountResponse = await hosted.Client.PostAsJsonAsync("/api/channels/accounts", accountRequest);
        createAccountResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var webhookRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/channels/webhook/webhook-dm-runtime/events")
        {
            Content = JsonContent.Create(new
            {
                eventType = "message.received",
                eventId = "event-runtime-dm-001",
                externalThreadId = "dm-thread-001",
                threadType = "directMessage",
                occurredAt = "2026-03-20T03:00:00Z",
                messageId = "message-runtime-dm-001",
                text = "Can you reply on my behalf?",
                sender = new
                {
                    id = "sender-dm-001",
                    displayName = "Alice",
                },
            }),
        };
        webhookRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
        webhookRequest.Headers.Add("X-KodaClaw-Webhook-Secret", "hook-secret");

        var webhookResponse = await hosted.Client.SendAsync(webhookRequest);
        webhookResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await webhookResponse.Content.ReadFromJsonAsync<ChannelThreadDetail>();
        detail.Should().NotBeNull();
        detail!.Binding.ThreadType.Should().Be(ChannelThreadType.DirectMessage);
        detail.PendingApprovalId.Should().BeNull();
        detail.HasPendingDraft.Should().BeFalse();
        detail.LastTurnOutcome.Should().NotBeNull();
        detail.LastTurnOutcome!.Kind.Should().Be(ChannelTurnOutcomeKind.Delivered);
        detail.LastTurnOutcome!.ReasonCode.Should().Be("full_agent_mode");
        detail.LastTurnOutcome.HasExplicitMention.Should().BeFalse();

        var threadsResponse = await hosted.Client.GetFromJsonAsync<ChannelsQueryResponse>(
            "/api/channels/threads?connectorKind=GenericWebhook&accountId=webhook-dm-runtime");
        threadsResponse.Should().NotBeNull();
        threadsResponse!.Items.Should().ContainSingle();
        threadsResponse.Items[0].LastTurnOutcome.Should().NotBeNull();
        threadsResponse.Items[0].LastTurnOutcome!.Kind.Should().Be(ChannelTurnOutcomeKind.Delivered);
        threadsResponse.Items[0].LastTurnOutcome!.ReasonCode.Should().Be("full_agent_mode");
    }

    [Fact]
    public async Task Generic_webhook_with_runtime_should_deliver_group_turn_in_full_agent_mode()
    {
        using var workspace = new TempWorkspaceRoot();
        var modelProvider = new StubChannelTurnModelProvider("Checking situation before replying.");
        await using var hosted = await StartGatewayAsync(workspace.Path, modelProvider, enableRuntime: true);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var accountRequest = new UpsertChannelAccountRequest(
            Id: "webhook-group-runtime",
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            DisplayName: "Runtime Group Webhook",
            ConfigurationJson: """{"sharedSecret":"hook-secret","defaultThreadType":"Group"}""");
        var createAccountResponse = await hosted.Client.PostAsJsonAsync("/api/channels/accounts", accountRequest);
        createAccountResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var webhookRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/channels/webhook/webhook-group-runtime/events")
        {
            Content = JsonContent.Create(new
            {
                eventType = "message.received",
                eventId = "event-runtime-group-001",
                externalThreadId = "group-thread-002",
                threadType = "group",
                occurredAt = "2026-03-20T04:00:00Z",
                messageId = "message-runtime-group-001",
                text = "What should we do next on this incident?",
                sender = new
                {
                    id = "sender-group-001",
                    displayName = "Ops Bridge",
                },
            }),
        };
        webhookRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
        webhookRequest.Headers.Add("X-KodaClaw-Webhook-Secret", "hook-secret");

        var webhookResponse = await hosted.Client.SendAsync(webhookRequest);
        webhookResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await webhookResponse.Content.ReadFromJsonAsync<ChannelThreadDetail>();
        detail.Should().NotBeNull();
        detail!.Binding.ThreadType.Should().Be(ChannelThreadType.Group);
        detail.PendingApprovalId.Should().BeNull();
        detail.HasPendingDraft.Should().BeFalse();
        detail.LastTurnOutcome.Should().NotBeNull();
        detail.LastTurnOutcome!.Kind.Should().Be(ChannelTurnOutcomeKind.Delivered);
        detail.LastTurnOutcome!.ReasonCode.Should().Be("full_agent_mode");
        detail.LastTurnOutcome.HasExplicitMention.Should().BeFalse();
    }

    [Fact]
    public async Task Telegram_account_create_should_auto_start_polling_and_orchestrate_inbound_turn()
    {
        using var workspace = new TempWorkspaceRoot();
        var fakeTelegram = new FakeTelegramApiClient(
        [
            [CreateDirectMessageUpdate(1001, 11, "Please reply on my behalf.")],
        ]);
        var modelProvider = new StubChannelTurnModelProvider("I will use channel_send to reply.");

        await using var hosted = await StartGatewayAsync(
            workspace.Path,
            modelProvider,
            enableRuntime: true,
            configureServices: services =>
            {
                services.AddSingleton<ITelegramApiClient>(fakeTelegram);
            });
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var createAccountResponse = await hosted.Client.PostAsJsonAsync(
            "/api/channels/accounts",
            new UpsertChannelAccountRequest(
                Id: "telegram-runtime",
                ConnectorKind: ChannelConnectorKind.Telegram,
                DisplayName: "Telegram Runtime",
                CredentialReference: "inline:telegram-token-runtime",
                ConfigurationJson: """{"defaultDeliveryMode":"DraftApproval"}"""));
        createAccountResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var account = await createAccountResponse.Content.ReadFromJsonAsync<ChannelAccount>();
        account.Should().NotBeNull();
        account!.State.Should().Be(ChannelAccountState.Connected);

        ChannelThreadSummary? thread = null;
        ChannelThreadDetail? detail = null;

        await EventuallyAsync(async () =>
        {
            var threads = await hosted.Client.GetFromJsonAsync<ChannelsQueryResponse>(
                "/api/channels/threads?connectorKind=Telegram&accountId=telegram-runtime&limit=20");
            thread = threads?.Items.SingleOrDefault();
            if (thread is null)
            {
                return false;
            }

            detail = await hosted.Client.GetFromJsonAsync<ChannelThreadDetail>(
                $"/api/channels/threads/{thread.BindingId}");
            return thread.LastTurnOutcome is not null
                && detail?.LastTurnOutcome is not null;
        }, "Timed out waiting for telegram polling to create a thread outcome.");

        thread.Should().NotBeNull();
        thread!.LastTurnOutcome.Should().NotBeNull();
        thread.LastTurnOutcome!.Kind.Should().Be(ChannelTurnOutcomeKind.Delivered);
        thread.LastTurnOutcome!.ReasonCode.Should().Be("full_agent_mode");

        detail.Should().NotBeNull();
        detail!.Binding.SessionKind.Should().Be(SessionKind.ChannelDirectMessage);
        detail.PendingApprovalId.Should().BeNull();
        detail.HasPendingDraft.Should().BeFalse();
        detail.LastTurnOutcome.Should().NotBeNull();
        detail.LastTurnOutcome!.Kind.Should().Be(ChannelTurnOutcomeKind.Delivered);
        detail.LastTurnOutcome!.ReasonCode.Should().Be("full_agent_mode");
    }

    [Fact]
    public async Task Full_agent_mode_dm_turn_should_always_deliver_without_pending_approval()
    {
        using var workspace = new TempWorkspaceRoot();
        var modelProvider = new StubChannelTurnModelProvider("I will use channel_send to deliver the reply.");
        await using var hosted = await StartGatewayAsync(workspace.Path, modelProvider, enableRuntime: true);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var createAccountResponse = await hosted.Client.PostAsJsonAsync(
            "/api/channels/accounts",
            new UpsertChannelAccountRequest(
                Id: "webhook-dm-full-agent",
                ConnectorKind: ChannelConnectorKind.GenericWebhook,
                DisplayName: "Webhook DM Full Agent",
                ConfigurationJson: """{"sharedSecret":"hook-secret","defaultThreadType":"DirectMessage","defaultDeliveryMode":"DraftApproval"}"""));
        createAccountResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var webhookRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/channels/webhook/webhook-dm-full-agent/events")
        {
            Content = JsonContent.Create(new
            {
                eventType = "message.received",
                eventId = "event-runtime-dm-full-agent-001",
                externalThreadId = "dm-thread-full-agent-001",
                threadType = "directMessage",
                occurredAt = "2026-03-20T05:00:00Z",
                messageId = "message-runtime-dm-full-agent-001",
                text = "Please respond for me.",
                sender = new
                {
                    id = "sender-dm-full-agent-001",
                    displayName = "Alice",
                },
            }),
        };
        webhookRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
        webhookRequest.Headers.Add("X-KodaClaw-Webhook-Secret", "hook-secret");

        var webhookResponse = await hosted.Client.SendAsync(webhookRequest);
        webhookResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await webhookResponse.Content.ReadFromJsonAsync<ChannelThreadDetail>();
        detail.Should().NotBeNull();

        // In Full Agent Mode, agent uses channel_send tool directly — no draft, no pending approval.
        detail!.PendingApprovalId.Should().BeNull();
        detail.HasPendingDraft.Should().BeFalse();
        detail.LastTurnOutcome.Should().NotBeNull();
        detail.LastTurnOutcome!.Kind.Should().Be(ChannelTurnOutcomeKind.Delivered);
        detail.LastTurnOutcome!.ReasonCode.Should().Be("full_agent_mode");
    }

    [Fact]
    public async Task Feishu_account_create_should_persist_and_appear_in_list()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var accountRequest = new UpsertChannelAccountRequest(
            Id: "feishu-main",
            ConnectorKind: ChannelConnectorKind.Feishu,
            DisplayName: "飞书 Bot",
            ConfigurationJson: """{"appId":"cli_abc123","appSecret":"inline-secret","defaultDeliveryMode":"RequireApproval"}""",
            InboundEnabled: false);

        var createResponse = await hosted.Client.PostAsJsonAsync("/api/channels/accounts", accountRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<ChannelAccount>();
        created.Should().NotBeNull();
        created!.Id.Should().Be("feishu-main");
        created.ConnectorKind.Should().Be(ChannelConnectorKind.Feishu);
        created.DisplayName.Should().Be("飞书 Bot");

        var listResponse = await hosted.Client.GetAsync("/api/channels/accounts?connectorKind=Feishu");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var accounts = await listResponse.Content.ReadFromJsonAsync<List<ChannelAccount>>();
        accounts.Should().NotBeNull();
        accounts!.Should().ContainSingle(a => a.Id == "feishu-main");
    }

    [Fact]
    public async Task Feishu_account_update_should_persist_display_name_change()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var createRequest = new UpsertChannelAccountRequest(
            Id: "feishu-update",
            ConnectorKind: ChannelConnectorKind.Feishu,
            DisplayName: "Initial Name",
            ConfigurationJson: """{"appId":"cli_update","appSecret":"inline-secret"}""",
            InboundEnabled: false);

        await hosted.Client.PostAsJsonAsync("/api/channels/accounts", createRequest);

        var updateRequest = createRequest with { DisplayName = "Updated Name" };
        var updateResponse = await hosted.Client.PostAsJsonAsync("/api/channels/accounts", updateRequest);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await updateResponse.Content.ReadFromJsonAsync<ChannelAccount>();
        updated.Should().NotBeNull();
        updated!.DisplayName.Should().Be("Updated Name");
    }

    [Fact]
    public async Task Feishu_account_create_should_reject_missing_display_name()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var badRequest = new UpsertChannelAccountRequest(
            Id: "feishu-bad",
            ConnectorKind: ChannelConnectorKind.Feishu,
            DisplayName: "  ",
            ConfigurationJson: """{"appId":"cli_bad","appSecret":"s3cr3t"}""");

        var response = await hosted.Client.PostAsJsonAsync("/api/channels/accounts", badRequest);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        error.Should().NotBeNull();
        error!.Code.Should().Be("validation.channel_account_display_name_required");
    }

    private static Task<HostedGateway> StartGatewayAsync(
        string workspaceRoot,
        IModelProvider? modelProvider = null,
        bool enableRuntime = false,
        Action<IServiceCollection>? configureServices = null)
    {
        return HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureServices: services =>
            {
                if (modelProvider is not null)
                {
                    services.AddSingleton(modelProvider);
                    services.AddSingleton<IModelProvider>(modelProvider);
                }

                configureServices?.Invoke(services);
            },
            configureConfiguration: configuration =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                };

                if (enableRuntime)
                {
                    values["KODACLAW_DEFAULT_MODEL"] = "gpt-4o-mini";
                    values["OPENAI_API_KEY"] = "stub-key";
                }

                configuration.AddInMemoryCollection(values);
            },
            useTestWorkspaceService: false);
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-channel-api",
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

    private sealed class StubChannelTurnModelProvider : IModelProvider
    {
        private readonly string _response;

        public StubChannelTurnModelProvider(string response)
        {
            _response = response;
        }

        public string ProviderName => "stub-channel-turn";

        public async IAsyncEnumerable<StreamChunk> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();

            yield return new StreamChunk
            {
                Type = StreamChunkType.TextDelta,
                TextDelta = _response,
            };
            yield return new StreamChunk
            {
                Type = StreamChunkType.MessageStop,
                StopReason = ModelStopReason.EndTurn,
                Usage = new TokenUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                },
            };
        }

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelResponse
            {
                Content =
                [
                    new TextContent
                    {
                        Text = _response,
                    },
                ],
                StopReason = ModelStopReason.EndTurn,
                Usage = new TokenUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                },
                Model = request.Model,
            });
        }

        public Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class FakeTelegramApiClient : ITelegramApiClient
    {
        private readonly Queue<IReadOnlyList<TelegramUpdate>> _batches = new();

        public FakeTelegramApiClient(IEnumerable<IReadOnlyList<TelegramUpdate>> batches)
        {
            foreach (var batch in batches)
            {
                _batches.Enqueue(batch);
            }
        }

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

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
            string botToken,
            long? offset,
            int timeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            if (_batches.Count > 0)
            {
                return _batches.Dequeue();
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
            return Task.FromResult(new TelegramSendMessageResult
            {
                MessageId = 1,
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
            return Task.FromResult(new TelegramSendMessageResult { MessageId = 2 });
        }

        public Task<TelegramSendMessageResult> SendAudioAsync(
            string botToken,
            long chatId,
            Stream audio,
            string contentType,
            string? caption,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TelegramSendMessageResult { MessageId = 3 });
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
            return Task.FromResult(new TelegramSendMessageResult { MessageId = 4 });
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

    private static TelegramUpdate CreateDirectMessageUpdate(int updateId, int messageId, string text)
    {
        return new TelegramUpdate
        {
            UpdateId = updateId,
            Message = new TelegramMessage
            {
                MessageId = messageId,
                DateUnixSeconds = 1_773_904_800 + updateId,
                Text = text,
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
        };
    }

    private static async Task EventuallyAsync(Func<Task<bool>> predicate, string failureMessage, int attempts = 120, int delayMs = 50)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (await predicate())
            {
                return;
            }

            await Task.Delay(delayMs);
        }

        throw new Xunit.Sdk.XunitException(failureMessage);
    }
}
