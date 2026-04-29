using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Settings;
using KodaClaw.Contracts.System;
using KodaClaw.ControlPlane;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class ApprovalApiIntegrationTests
{
    private const string GatewayToken = "test-token";

    [Fact]
    public async Task Approvals_list_should_return_pending_items()
    {
        using var workspace = new TempWorkspaceRoot();
        await SeedApprovalAsync(workspace.Path, new Approval(
            Id: "approval-001",
            Kind: ApprovalKind.ExternalAction,
            Status: ApprovalStatus.Pending,
            Title: "Approve outbound reply",
            Summary: "The runtime is waiting on a dangerous tool.",
            Source: "runtime.main_session.approval",
            RequestedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            SessionId: "session-main",
            CorrelationId: "corr-approval-001",
            InboxItemId: "inbox-approval-001",
            PayloadJson: "{\"callId\":\"call-approval-001\"}"),
            new InboxItem(
                Id: "inbox-approval-001",
                Kind: InboxItemKind.Approval,
                Status: InboxItemStatus.Open,
                Title: "Approve outbound reply",
                Summary: "Awaiting approval.",
                Source: "runtime.main_session.approval",
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                RequiresAction: true,
                Route: "/approvals/approval-001",
                SessionId: "session-main",
                ApprovalId: "approval-001"));

        await SeedApprovalAsync(workspace.Path, new Approval(
            Id: "approval-002",
            Kind: ApprovalKind.PluginAuthorization,
            Status: ApprovalStatus.Approved,
            Title: "Plugin approval",
            Summary: "Already approved.",
            Source: "runtime.main_session.approval",
            RequestedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            SessionId: "session-main"),
            null);

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.GetAsync("/api/approvals?status=Pending&sessionId=session-main&limit=5");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ApprovalQueryResponse>();
        payload.Should().NotBeNull();
        payload!.Items.Select(item => item.Id).Should().ContainSingle("approval-001");
    }

    [Fact]
    public async Task Approval_detail_should_return_not_found_for_missing()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.GetAsync("/api/approvals/missing");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Approval_detail_should_return_approval()
    {
        using var workspace = new TempWorkspaceRoot();
        var approval = new Approval(
            Id: "approval-001",
            Kind: ApprovalKind.ChannelDelivery,
            Status: ApprovalStatus.Pending,
            Title: "Channel delivery",
            Summary: "Pipeline waiting.",
            Source: "runtime.main_session.approval",
            RequestedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            SessionId: "session-b",
            CorrelationId: "corr-detail-001");
        await SeedApprovalAsync(workspace.Path, approval, null);

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.GetAsync("/api/approvals/approval-001");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<Approval>();
        payload.Should().NotBeNull();
        payload!.Id.Should().Be(approval.Id);
        payload.Status.Should().Be(ApprovalStatus.Pending);
        payload.SessionId.Should().Be("session-b");
    }

    [Fact]
    public async Task Approval_approve_without_live_session_returns_conflict()
    {
        using var workspace = new TempWorkspaceRoot();
        await SeedApprovalAsync(workspace.Path, new Approval(
            Id: "approval-pending",
            Kind: ApprovalKind.ExternalAction,
            Status: ApprovalStatus.Pending,
            Title: "External action",
            Summary: "Awaiting live session.",
            Source: "runtime.main_session.approval",
            RequestedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            SessionId: "session-main",
            InboxItemId: "inbox-pending"));

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/approvals/approval-pending/approve",
            new ApprovalDecisionRequest("no live session"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Approval_approve_with_live_session_transitions_status()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayWithRuntimeAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var registry = hosted.Services.GetRequiredService<IToolRegistry>();
        registry.Register(new DangerTool());

        // Default settings have AutoApproveToolCalls=true, which would bypass RequireApprovalTools.
        // Disable it so the danger_tool triggers a pending approval.
        await hosted.Services.GetRequiredService<ISettingsRepository>().SaveAsync(
            KodaClawSettings.Default with { AutoApproveToolCalls = false });

        var approvals = hosted.Services.GetRequiredService<IApprovalRepository>();
        var inbox = hosted.Services.GetRequiredService<IInboxRepository>();
        var runtime = hosted.Services.GetRequiredService<IMainSessionService>();
        var handle = await runtime.EnsureMainSessionAsync();
        var runTask = handle.Agent.RunAsync("Please execute the dangerous tool.");

        var approval = await WaitForPendingApprovalAsync(approvals, handle.SessionId);
        approval.Status.Should().Be(ApprovalStatus.Pending);

        var response = await hosted.Client.PostAsJsonAsync(
            $"/api/approvals/{approval.Id}/approve",
            new ApprovalDecisionRequest("approved via gateway"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var decided = await response.Content.ReadFromJsonAsync<Approval>();
        decided.Should().NotBeNull();
        decided!.Status.Should().Be(ApprovalStatus.Approved);

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        result.Success.Should().BeTrue();

        await WaitForInboxStatusAsync(inbox, approval.InboxItemId!, InboxItemStatus.Resolved);

        var resolved = await approvals.GetByIdAsync(approval.Id);
        resolved.Should().NotBeNull();
        resolved!.Status.Should().Be(ApprovalStatus.Approved);
    }

    [Fact]
    public async Task Approval_reject_without_live_session_transitions_status()
    {
        using var workspace = new TempWorkspaceRoot();
        await SeedApprovalAsync(workspace.Path, new Approval(
            Id: "approval-reject",
            Kind: ApprovalKind.ExternalAction,
            Status: ApprovalStatus.Pending,
            Title: "Rejectable action",
            Summary: "No live session.",
            Source: "runtime.main_session.approval",
            RequestedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            SessionId: "session-main",
            InboxItemId: "inbox-reject"));

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/approvals/approval-reject/reject",
            new ApprovalDecisionRequest("reject without live session"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var decided = await response.Content.ReadFromJsonAsync<Approval>();
        decided.Should().NotBeNull();
        decided!.Status.Should().Be(ApprovalStatus.Rejected);
    }

    [Fact]
    public async Task Approval_reject_non_pending_returns_conflict()
    {
        using var workspace = new TempWorkspaceRoot();
        await SeedApprovalAsync(workspace.Path, new Approval(
            Id: "approval-rejected",
            Kind: ApprovalKind.OutboundMessage,
            Status: ApprovalStatus.Approved,
            Title: "Already decided",
            Summary: "Cannot reject.",
            Source: "runtime.main_session.approval",
            RequestedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            SessionId: "session-main"));

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/approvals/approval-rejected/reject",
            new ApprovalDecisionRequest("still reject"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    private static Task<HostedGateway> StartGatewayAsync(
        string workspaceRoot,
        Action<IServiceCollection>? configureServices = null,
        Action<IConfigurationBuilder>? configureConfiguration = null)
    {
        return HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureServices: services => configureServices?.Invoke(services),
            configureConfiguration: configuration =>
            {
                configureConfiguration?.Invoke(configuration);
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                });
            },
            useTestWorkspaceService: false);
    }

    private static Task<HostedGateway> StartGatewayWithRuntimeAsync(string workspaceRoot)
    {
        return StartGatewayAsync(
            workspaceRoot,
            configureServices: services =>
            {
                services.AddSingleton<IModelProvider>(new ApprovalDrivenModelProvider());
                services.AddSingleton(new MainSessionOptions
                {
                    Model = "approval-model",
                    MaxIterations = 8,
                    Tools = new[] { "danger_tool" },
                    Permissions = new PermissionConfig
                    {
                        Mode = "auto",
                        RequireApprovalTools = new[] { "danger_tool" },
                    },
                });
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_DEFAULT_MODEL"] = "approval-model",
                    ["OPENAI_API_KEY"] = "stub-key",
                });
            });
    }

    private static async Task SeedApprovalAsync(string workspaceRoot, Approval approval, InboxItem? inboxItem = null)
    {
        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        services.AddKodaClawJsonStore(workspaceRoot);
        services.AddKodaClawControlPlane();
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<IApprovalRepository>();
        await repository.UpsertAsync(approval);
        if (inboxItem is not null)
        {
            var inboxRepository = provider.GetRequiredService<IInboxRepository>();
            await inboxRepository.UpsertAsync(inboxItem);
        }
    }

    private static Task<Approval> WaitForPendingApprovalAsync(IApprovalRepository approvals, string sessionId)
    {
        return EventuallyAsync(async () =>
        {
            var pending = await approvals.ListAsync(new ApprovalQuery(
                Status: ApprovalStatus.Pending,
                SessionId: sessionId,
                Limit: 10));
            return pending.SingleOrDefault();
        }, "Timed out waiting for pending approval.");
    }

    private static Task<InboxItem> WaitForInboxStatusAsync(IInboxRepository inboxRepository, string inboxId, InboxItemStatus status)
    {
        return EventuallyAsync(async () =>
        {
            var item = await inboxRepository.GetByIdAsync(inboxId);
            return item?.Status == status ? item : null;
        }, $"Timed out waiting for inbox item '{inboxId}' to reach status '{status}'.");
    }

    private static async Task<T> EventuallyAsync<T>(
        Func<Task<T?>> probe,
        string failureMessage,
        int attempts = 100,
        int delayMs = 50)
        where T : class
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var result = await probe();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(delayMs);
        }

        throw new Xunit.Sdk.XunitException(failureMessage);
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-approval-api",
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

    private sealed class ApprovalDrivenModelProvider : IModelProvider
    {
        private const string ApprovalCallId = "call-danger-001";

        public string ProviderName => "approval-stub";

#pragma warning disable CS1998 // 异步方法缺少 "await" 运算符，将以同步方式运行
        public async IAsyncEnumerable<StreamChunk> StreamAsync(
#pragma warning restore CS1998 // 异步方法缺少 "await" 运算符，将以同步方式运行
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var hasToolResult = request.Messages
                .SelectMany(static message => message.Content)
                .OfType<ToolResultContent>()
                .Any(static item => item.ToolUseId == ApprovalCallId);

            if (!hasToolResult)
            {
                yield return new StreamChunk
                {
                    Type = StreamChunkType.ToolUseStart,
                    ToolUse = new ToolUseChunk
                    {
                        Id = ApprovalCallId,
                        Name = "danger_tool",
                    },
                };
                yield return new StreamChunk
                {
                    Type = StreamChunkType.ToolUseInputDelta,
                    ToolUse = new ToolUseChunk
                    {
                        Id = ApprovalCallId,
                        InputDelta = "{\"path\":\"/tmp/demo.txt\"}",
                    },
                };
                yield return new StreamChunk
                {
                    Type = StreamChunkType.ToolUseComplete,
                    ToolUse = new ToolUseChunk
                    {
                        Id = ApprovalCallId,
                    },
                };
                yield return new StreamChunk
                {
                    Type = StreamChunkType.MessageStop,
                    StopReason = ModelStopReason.ToolUse,
                    Usage = new TokenUsage
                    {
                        InputTokens = 0,
                        OutputTokens = 0,
                    },
                };
                yield break;
            }

            yield return new StreamChunk
            {
                Type = StreamChunkType.TextDelta,
                TextDelta = "approved",
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
                Content = new[]
                {
                    new TextContent
                    {
                        Text = "approved",
                    },
                },
                StopReason = ModelStopReason.EndTurn,
                Usage = new TokenUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                },
                Model = request.Model,
            });
        }

        public Task<bool> ValidateAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class DangerTool : ToolBase<DangerToolArgs>
    {
        public override string Name => "danger_tool";

        public override string Description => "Writes a dangerous side effect after approval.";

        public override object InputSchema => new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string" },
            },
            required = new[] { "path" },
        };

        public override ToolAttributes Attributes => new()
        {
            RequiresApproval = true,
        };

        protected override Task<ToolResult> ExecuteAsync(
            DangerToolArgs arguments,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(ToolResult.Ok(new
            {
                status = "executed",
                path = arguments.Path,
            }));
        }
    }

    private sealed class DangerToolArgs
    {
        public required string Path { get; init; }
    }
}
