using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Connectors.Telegram;
using KodaClaw.ChannelHub.Delivery;
using KodaClaw.ChannelHub.Inbound;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Workspace;
using KodaClaw.IntegrationTests.Gateway;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Smoke;

public sealed class Iteration5DirectMessageAcceptanceIntegrationTests
{
    private const string GatewayToken = "test-token";

    [Fact]
    public async Task Iteration_5_dm_acceptance_should_cover_telegram_dm_binding_session_approval_and_delivery()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-iteration5-dm");
        await PrepareWorkspaceAsync(workspace.RootPath);

        var fakeTelegram = new FakeTelegramApiClient(
        [
            [CreateDirectMessageUpdate(1001, 11, "hello from telegram")],
            [CreateDirectMessageUpdate(1002, 12, "follow-up from same thread")],
        ]);
        var modelProvider = new CapturingModelProvider();

        await using var hosted = await StartGatewayAsync(workspace.RootPath, modelProvider, fakeTelegram);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var connectors = await hosted.Client.GetFromJsonAsync<List<ChannelConnectorDescriptor>>("/api/channels/connectors");
        connectors.Should().NotBeNull();
        connectors!.Should().Contain(item =>
            item.Kind == ChannelConnectorKind.Telegram &&
            item.Implemented &&
            item.SupportsInbound &&
            item.SupportsOutbound);

        var createAccountResponse = await hosted.Client.PostAsJsonAsync(
            "/api/channels/accounts",
            new UpsertChannelAccountRequest(
                Id: "telegram-main",
                ConnectorKind: ChannelConnectorKind.Telegram,
                DisplayName: "Telegram Bot",
                CredentialReference: "inline:telegram-token-acceptance",
                InboundEnabled: false,
                ConfigurationJson: """{"defaultDeliveryMode":"DraftApproval"}"""));
        createAccountResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var account = await createAccountResponse.Content.ReadFromJsonAsync<ChannelAccount>();
        account.Should().NotBeNull();
        account!.ConnectorKind.Should().Be(ChannelConnectorKind.Telegram);

        var connector = hosted.Services.GetRequiredService<TelegramConnector>();
        var ingestion = hosted.Services.GetRequiredService<ChannelEventIngestionService>();
        var governance = hosted.Services.GetRequiredService<ChannelDeliveryGovernanceService>();
        var channelSessionService = hosted.Services.GetRequiredService<IChannelSessionService>();
        var approvals = hosted.Services.GetRequiredService<IApprovalRepository>();
        var threadBindings = hosted.Services.GetRequiredService<IThreadBindingRepository>();

        var inboundResults = new ConcurrentQueue<ChannelInboundProcessingResult>();
        var handles = new ConcurrentQueue<ChannelSessionHandle>();

        await connector.StartAsync(
            account,
            async (envelope, cancellationToken) =>
            {
                var processing = await ingestion.IngestAsync(envelope, cancellationToken);
                inboundResults.Enqueue(processing);

                await WriteThreadSummaryAsync(
                    workspace.RootPath,
                    processing.Binding.Id,
                    "DM summary anchor: customer asked for a concise update.");

                var handle = await channelSessionService.EnsureChannelSessionAsync(
                    processing.Binding,
                    processing.Policy,
                    cancellationToken);
                handles.Enqueue(handle);
            });

        try
        {
            await EventuallyAsync(
                () => inboundResults.Count >= 2,
                "Timed out waiting for telegram DM inbound events.");

            var processingItems = inboundResults.ToArray();
            processingItems.Should().HaveCount(2);
            processingItems[0].CreatedBinding.Should().BeTrue();
            processingItems[1].CreatedBinding.Should().BeFalse();
            processingItems[1].Binding.Id.Should().Be(processingItems[0].Binding.Id);
            processingItems[1].Binding.SessionId.Should().Be(processingItems[0].Binding.SessionId);
            processingItems[1].Binding.SessionKind.Should().Be(SessionKind.ChannelDirectMessage);
            processingItems[1].Binding.LastMessagePreview.Should().Be("follow-up from same thread");

            var binding = processingItems[1].Binding;
            var storedBinding = await threadBindings.GetByIdAsync(binding.Id);
            storedBinding.Should().NotBeNull();
            storedBinding!.SessionId.Should().Be(binding.SessionId);
            storedBinding.SessionKind.Should().Be(SessionKind.ChannelDirectMessage);

            var threadList = await hosted.Client.GetFromJsonAsync<ChannelsQueryResponse>(
                "/api/channels/threads?connectorKind=Telegram&limit=20");
            threadList.Should().NotBeNull();
            threadList!.Items.Should().ContainSingle(item =>
                item.BindingId == binding.Id &&
                item.SessionKind == SessionKind.ChannelDirectMessage &&
                item.SessionId == binding.SessionId);

            var handlesSnapshot = handles.ToArray();
            handlesSnapshot.Should().NotBeEmpty();
            var handle = handlesSnapshot[^1];
            var inspectResult = await handle.Agent.RunAsync("inspect");
            inspectResult.Success.Should().BeTrue();

            modelProvider.LastRequest.Should().NotBeNull();
            var prompt = modelProvider.LastRequest!.SystemPrompt;
            prompt.Should().Contain("ThreadType: DirectMessage");
            prompt.Should().Contain("SessionKind: ChannelDirectMessage");
            prompt.Should().Contain("### File: workspace/USER.md");
            prompt.Should().Contain("DM summary anchor: customer asked for a concise update.");
            // KC-5001: DM sessions now load full workspace context including MEMORY.md.
            prompt.Should().Contain("### File: workspace/MEMORY.md");
            prompt.Should().Contain("Memory anchor: do not leak this.");

            var sessionResponse = await hosted.Client.GetAsync($"/api/sessions/{binding.SessionId}");
            sessionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var session = await sessionResponse.Content.ReadFromJsonAsync<SessionDetail>();
            session.Should().NotBeNull();
            session!.SessionKind.Should().Be(SessionKind.ChannelDirectMessage);

            var draft = new ChannelOutboundDraft(
                DraftId: $"draft-{binding.Id}",
                BindingId: binding.Id,
                ConnectorKind: binding.ConnectorKind,
                AccountId: binding.AccountId,
                ExternalThreadId: binding.ExternalThreadId,
                MessageText: "Thanks, we are on it.",
                DeliveryMode: processingItems[1].DeliveryRule.Mode,
                CreatedAt: DateTimeOffset.UtcNow,
                SessionId: binding.SessionId,
                CorrelationId: "corr-iteration5-dm");

            var evaluation = await governance.EvaluateAsync(binding, processingItems[1].DeliveryRule, draft);
            evaluation.Disposition.Should().Be(ChannelDeliveryDisposition.ApprovalRequired);
            evaluation.ApprovalId.Should().NotBeNull();
            evaluation.InboxItemId.Should().NotBeNull();

            var pendingApproval = await approvals.GetByIdAsync(evaluation.ApprovalId!);
            pendingApproval.Should().NotBeNull();
            pendingApproval!.Kind.Should().Be(ApprovalKind.ChannelDelivery);
            pendingApproval.InboxItemId.Should().Be(evaluation.InboxItemId);

            var detailBeforeDecision = await hosted.Client.GetFromJsonAsync<ChannelThreadDetail>(
                $"/api/channels/threads/{binding.Id}");
            detailBeforeDecision.Should().NotBeNull();
            detailBeforeDecision!.HasPendingDraft.Should().BeTrue();
            detailBeforeDecision.PendingApprovalId.Should().Be(evaluation.ApprovalId);

            var approveResponse = await hosted.Client.PostAsJsonAsync(
                $"/api/approvals/{evaluation.ApprovalId}/approve",
                new ApprovalDecisionRequest("approve DM delivery"));
            approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var approved = await approveResponse.Content.ReadFromJsonAsync<Approval>();
            approved.Should().NotBeNull();
            approved!.Status.Should().Be(ApprovalStatus.Approved);

            fakeTelegram.SendCalls.Should().ContainSingle();
            fakeTelegram.SendCalls[0].Token.Should().Be("telegram-token-acceptance");
            fakeTelegram.SendCalls[0].ChatId.Should().Be(10001);
            fakeTelegram.SendCalls[0].Text.Should().Be("Thanks, we are on it.");

            var detailAfterDecision = await hosted.Client.GetFromJsonAsync<ChannelThreadDetail>(
                $"/api/channels/threads/{binding.Id}");
            detailAfterDecision.Should().NotBeNull();
            detailAfterDecision!.HasPendingDraft.Should().BeFalse();
            detailAfterDecision.PendingApprovalId.Should().BeNull();
            detailAfterDecision.Binding.LastOutboundAt.Should().NotBeNull();

            var audit = await hosted.Client.GetFromJsonAsync<List<ChannelAuditEntry>>(
                $"/api/channels/threads/{binding.Id}/audit?limit=20");
            audit.Should().NotBeNull();
            audit!.Select(item => item.EventType).Should().Contain(new[]
            {
                "message.received",
                "approval.approved",
                "delivery.sent",
            });
        }
        finally
        {
            await connector.StopAsync(account.Id);
        }
    }

    private static Task<HostedGateway> StartGatewayAsync(
        string workspaceRoot,
        CapturingModelProvider modelProvider,
        FakeTelegramApiClient fakeTelegramApiClient)
    {
        return HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureServices: services =>
            {
                services.AddSingleton<IModelProvider>(modelProvider);
                services.AddSingleton<ITelegramApiClient>(fakeTelegramApiClient);
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                    ["KODACLAW_DEFAULT_MODEL"] = "gpt-4o-mini",
                    ["OPENAI_API_KEY"] = "stub-key",
                });
            },
            useTestWorkspaceService: false);
    }

    private static async Task PrepareWorkspaceAsync(string workspaceRoot)
    {
        var workspace = new WorkspaceService(new KodaClawWorkspaceOptions
        {
            RootPath = workspaceRoot,
        });

        await workspace.EnsureInitializedAsync();
        await workspace.SaveAppConfigAsync(new WorkspaceAppConfig
        {
            WorkspaceVersion = KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
            BootstrapCompleted = true,
            ActiveMainSessionId = null,
        });

        await WriteWorkspaceFileAsync(workspaceRoot, KodaClawWorkspaceLayout.AgentsFile, "# Agents\n\n- Agent anchor: stay inspectable.\n");
        await WriteWorkspaceFileAsync(workspaceRoot, KodaClawWorkspaceLayout.IdentityFile, "# Identity\n\n- Identity anchor: local-first.\n");
        await WriteWorkspaceFileAsync(workspaceRoot, KodaClawWorkspaceLayout.SoulFile, "# Soul\n\n- Soul anchor: calm and practical.\n");
        await WriteWorkspaceFileAsync(workspaceRoot, KodaClawWorkspaceLayout.UserFile, "# User\n\n- User anchor: prefers concise replies.\n");
        await WriteWorkspaceFileAsync(workspaceRoot, KodaClawWorkspaceLayout.MemoryFile, "# Memory\n\n- Memory anchor: do not leak this.\n");
    }

    private static async Task WriteThreadSummaryAsync(string workspaceRoot, string bindingId, string content)
    {
        var summaryPath = Path.Combine(
            workspaceRoot,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            "channels",
            bindingId,
            "SUMMARY.md");
        Directory.CreateDirectory(Path.GetDirectoryName(summaryPath)!);
        await File.WriteAllTextAsync(summaryPath, content);
    }

    private static async Task WriteWorkspaceFileAsync(string workspaceRoot, string fileName, string content)
    {
        var path = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.WorkspaceDirectory, fileName);
        await File.WriteAllTextAsync(path, content);
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

    private static async Task EventuallyAsync(Func<bool> predicate, string failureMessage, int attempts = 120, int delayMs = 50)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(delayMs);
        }

        throw new Xunit.Sdk.XunitException(failureMessage);
    }

    private sealed class CapturingModelProvider : IModelProvider
    {
        public string ProviderName => "capturing";

        public ModelRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<StreamChunk> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            await Task.Yield();

            yield return new StreamChunk
            {
                Type = StreamChunkType.TextDelta,
                TextDelta = "channel-dm-ok",
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
            LastRequest = request;
            return Task.FromResult(new ModelResponse
            {
                Content =
                [
                    new TextContent
                    {
                        Text = "channel-dm-ok",
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

        public ModelCapabilities? GetModelCapabilities(string modelId) => null;
    }

    private sealed class FakeTelegramApiClient : ITelegramApiClient
    {
        private readonly ConcurrentQueue<IReadOnlyList<TelegramUpdate>> _updatesBatches = new();

        public FakeTelegramApiClient(IEnumerable<IReadOnlyList<TelegramUpdate>> updatesBatches)
        {
            foreach (var batch in updatesBatches)
            {
                _updatesBatches.Enqueue(batch);
            }
        }

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

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
            string botToken,
            long? offset,
            int timeoutSeconds,
            CancellationToken cancellationToken = default)
        {
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
            long? replyToMessageId = null,
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
            long? replyToMessageId = null,
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
            long? replyToMessageId = null,
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
            long? replyToMessageId = null,
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

    private sealed record ChannelConnectorDescriptor(
        ChannelConnectorKind Kind,
        string DisplayName,
        bool Implemented,
        bool SupportsInbound,
        bool SupportsOutbound,
        bool ProductOwned);

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot(string prefix)
        {
            RootPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
