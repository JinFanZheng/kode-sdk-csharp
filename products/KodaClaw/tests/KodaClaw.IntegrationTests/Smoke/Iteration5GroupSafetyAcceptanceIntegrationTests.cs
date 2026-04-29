using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Delivery;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Inbox;
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

public sealed class Iteration5GroupSafetyAcceptanceIntegrationTests
{
    private const string GatewayToken = "test-token";
    private const string SharedSecret = "hook-secret";

    [Fact]
    public async Task Iteration_5_group_safety_acceptance_should_guard_group_channel_boundaries()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-iteration5-group-safety");
        await PrepareWorkspaceAsync(workspace.Path);
        await using var hosted = await StartGatewayWithRuntimeAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);

        var accountRequest = new UpsertChannelAccountRequest(
            Id: "webhook-group-safety",
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            DisplayName: "Group Safety Webhook",
            ConfigurationJson: """
                {
                  "sharedSecret": "hook-secret",
                  "defaultThreadType": "Group"
                }
                """);
        var accountResponse = await hosted.Client.PostAsJsonAsync("/api/channels/accounts", accountRequest);
        accountResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var threadId = "group-thread-safety-001";
        var firstDetail = await PostChannelWebhookEventAsync(
            hosted.Client,
            accountRequest.Id,
            threadId,
            eventId: "event-001",
            messageId: "message-001",
            text: "first group message");
        firstDetail.Binding.ThreadType.Should().Be(ChannelThreadType.Group);
        firstDetail.Binding.SessionKind.Should().Be(SessionKind.ChannelGroup);
        firstDetail.Binding.SessionId.Should().StartWith("channel-group-");
        firstDetail.Session.Should().NotBeNull();
        firstDetail.Session!.SessionKind.Should().Be(SessionKind.ChannelGroup);

        var channelSessionService = hosted.Services.GetRequiredService<IChannelSessionService>();
        var capturingProvider = hosted.Services.GetRequiredService<CapturingModelProvider>();
        capturingProvider.Clear();
        var handle = await channelSessionService.EnsureChannelSessionAsync(firstDetail.Binding, firstDetail.Policy);
        var result = await handle.Agent.RunAsync("inspect");
        result.Success.Should().BeTrue();

        capturingProvider.LastRequest.Should().NotBeNull();
        var prompt = capturingProvider.LastRequest!.SystemPrompt;
        prompt.Should().Contain("ThreadType: Group");
        prompt.Should().Contain("SessionKind: ChannelGroup");
        prompt.Should().Contain("### File: workspace/AGENTS.md");
        prompt.Should().Contain("### File: workspace/IDENTITY.md");
        prompt.Should().Contain("### File: workspace/SOUL.md");
        prompt.Should().NotContain("workspace/USER.md");
        prompt.Should().NotContain("workspace/MEMORY.md");
        prompt.Should().NotContain("User anchor: do not load in group.");
        prompt.Should().NotContain("Memory anchor: do not leak this.");
        prompt.Should().NotContain("Main session");

        var approvalsRepo = hosted.Services.GetRequiredService<IApprovalRepository>();

        var secondDetail = await PostChannelWebhookEventAsync(
            hosted.Client,
            accountRequest.Id,
            threadId,
            eventId: "event-002",
            messageId: "message-002",
            text: "second group whisper");
        secondDetail.Binding.Id.Should().Be(firstDetail.Binding.Id);
        secondDetail.Binding.SessionId.Should().Be(firstDetail.Binding.SessionId);

        var deliveryGovernance = hosted.Services.GetRequiredService<ChannelDeliveryGovernanceService>();
        var draft = new ChannelOutboundDraft(
            DraftId: Guid.NewGuid().ToString("N"),
            BindingId: secondDetail.Binding.Id,
            ConnectorKind: secondDetail.Binding.ConnectorKind,
            AccountId: secondDetail.Binding.AccountId,
            ExternalThreadId: secondDetail.Binding.ExternalThreadId,
            MessageText: "safe group reply",
            DeliveryMode: secondDetail.DeliveryRule.Mode,
            CreatedAt: DateTimeOffset.UtcNow);

        var evaluation = await deliveryGovernance.EvaluateAsync(
            secondDetail.Binding,
            secondDetail.DeliveryRule,
            draft);
        evaluation.Disposition.Should().Be(ChannelDeliveryDisposition.ApprovalRequired);
        var approvalId = evaluation.ApprovalId;
        approvalId.Should().NotBeNullOrWhiteSpace();

        var secondApproval = await approvalsRepo.GetByIdAsync(approvalId!, CancellationToken.None);
        secondApproval.Should().NotBeNull();
        secondApproval!.Kind.Should().Be(ApprovalKind.ChannelDelivery);

        var inbox = hosted.Services.GetRequiredService<IInboxRepository>();
        var inboxItem = await inbox.GetByIdAsync(secondApproval.InboxItemId!);
        inboxItem.Should().NotBeNull();
        inboxItem!.Kind.Should().Be(InboxItemKind.ChannelUpdate);
        inboxItem.Status.Should().Be(InboxItemStatus.Open);

        var bindingRepo = hosted.Services.GetRequiredService<IThreadBindingRepository>();
        var initialBinding = await bindingRepo.GetByIdAsync(firstDetail.Binding.Id);
        initialBinding.Should().NotBeNull();
        initialBinding!.LastOutboundAt.Should().BeNull();

        var threadDetail = await hosted.Client.GetFromJsonAsync<ChannelThreadDetail>($"/api/channels/threads/{firstDetail.Binding.Id}");
        threadDetail.Should().NotBeNull();
        threadDetail!.PendingApprovalId.Should().Be(secondApproval.Id);
        threadDetail.HasPendingDraft.Should().BeTrue();
        threadDetail.Session.Should().NotBeNull();
        threadDetail.Session!.SessionKind.Should().Be(SessionKind.ChannelGroup);

        var rejectResponse = await hosted.Client.PostAsJsonAsync(
            $"/api/approvals/{secondApproval.Id}/reject",
            new ApprovalDecisionRequest("reject group safety test"));
        rejectResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var rejected = await rejectResponse.Content.ReadFromJsonAsync<Approval>();
        rejected.Should().NotBeNull();
        rejected!.Status.Should().Be(ApprovalStatus.Rejected);

        var reloadedBinding = await bindingRepo.GetByIdAsync(firstDetail.Binding.Id);
        reloadedBinding.Should().NotBeNull();
        reloadedBinding!.LastOutboundAt.Should().BeNull();

        var auditResponse = await hosted.Client.GetAsync(
            $"/api/channels/threads/{firstDetail.Binding.Id}/audit?limit=20");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var auditEntries = await auditResponse.Content.ReadFromJsonAsync<List<ChannelAuditEntry>>();
        auditEntries.Should().NotBeNull();
        auditEntries!.Any(entry => entry.EventType == "message.received").Should().BeTrue();
        auditEntries!.Any(entry => entry.EventType == "approval.rejected").Should().BeTrue();
        auditEntries!.Any(entry => entry.EventType == "delivery.sent").Should().BeFalse();

        var detailAfterReject = await hosted.Client.GetFromJsonAsync<ChannelThreadDetail>($"/api/channels/threads/{firstDetail.Binding.Id}");
        detailAfterReject.Should().NotBeNull();
        detailAfterReject!.PendingApprovalId.Should().BeNull();
        detailAfterReject.HasPendingDraft.Should().BeFalse();
        detailAfterReject.Session.Should().NotBeNull();
        detailAfterReject.Session!.SessionKind.Should().Be(SessionKind.ChannelGroup);
        detailAfterReject.LastTurnOutcome.Should().NotBeNull();
        detailAfterReject.LastTurnOutcome!.Kind.Should().Be(ChannelTurnOutcomeKind.NoAction);
        detailAfterReject.LastTurnOutcome!.ReasonCode.Should().Be("approval_rejected");
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

        await WriteWorkspaceFileAsync(workspaceRoot, KodaClawWorkspaceLayout.AgentsFile, "# Agents\n\n- Agents anchor: keep answers bounded.\n");
        await WriteWorkspaceFileAsync(workspaceRoot, KodaClawWorkspaceLayout.IdentityFile, "# Identity\n\n- Identity anchor: KodaClaw is local-first.\n");
        await WriteWorkspaceFileAsync(workspaceRoot, KodaClawWorkspaceLayout.SoulFile, "# Soul\n\n- Soul anchor: practical and calm.\n");
        await WriteWorkspaceFileAsync(workspaceRoot, KodaClawWorkspaceLayout.UserFile, "# User\n\n- User anchor: do not load in group.\n");
        await WriteWorkspaceFileAsync(workspaceRoot, KodaClawWorkspaceLayout.MemoryFile, "# Memory\n\n- Memory anchor: do not leak this.\n");
    }

    private static async Task<ChannelThreadDetail> PostChannelWebhookEventAsync(
        HttpClient client,
        string accountId,
        string externalThreadId,
        string eventId,
        string messageId,
        string text)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/channels/webhook/{accountId}/events")
        {
            Content = JsonContent.Create(new
            {
                eventType = "message.received",
                eventId,
                externalThreadId,
                threadType = "group",
                occurredAt = DateTimeOffset.UtcNow.ToString("o"),
                messageId,
                text,
                sender = new
                {
                    id = "sender-group",
                    displayName = "Group Bot"
                }
            })
        };
        request.Headers.Add("X-KodaClaw-Webhook-Secret", SharedSecret);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<ChannelThreadDetail>();
        detail.Should().NotBeNull();
        return detail!;
    }

    private static Task WriteWorkspaceFileAsync(string workspaceRoot, string fileName, string content)
    {
        var path = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.WorkspaceDirectory, fileName);
        return File.WriteAllTextAsync(path, content);
    }


    private static Task<HostedGateway> StartGatewayWithRuntimeAsync(string workspaceRoot)
    {
        return HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureServices: services =>
            {
                var capturingProvider = new CapturingModelProvider();
                services.AddSingleton(capturingProvider);
                services.AddSingleton<IModelProvider>(capturingProvider);
                services.AddSingleton(new MainSessionOptions
                {
                    Model = "group-safety-model",
                    SystemPrompt = "You are KodaClaw main assistant for safety smoke.",
                    MaxIterations = 6
                });
                services.AddSingleton(new ChannelSessionOptions
                {
                    Model = "group-safety-model",
                    SystemPrompt = "You are KodaClaw channel assistant for group safety smoke.",
                    MaxIterations = 6
                });
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                    ["KODACLAW_DEFAULT_MODEL"] = "group-safety-model",
                    ["OPENAI_API_KEY"] = "stub-key"
                });
            },
            useTestWorkspaceService: false);
    }

    private sealed class CapturingModelProvider : IModelProvider
    {
        public string ProviderName => "capturing";

        public ModelRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<StreamChunk> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            await Task.Yield();

            yield return new StreamChunk
            {
                Type = StreamChunkType.TextDelta,
                TextDelta = "group-safety-ok"
            };
            yield return new StreamChunk
            {
                Type = StreamChunkType.MessageStop,
                StopReason = ModelStopReason.EndTurn,
                Usage = new TokenUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0
                }
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
                        Text = "group-safety-ok"
                    }
                ],
                StopReason = ModelStopReason.EndTurn,
                Usage = new TokenUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0
                },
                Model = request.Model
            });
        }

        public Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public void Clear() => LastRequest = null;
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
