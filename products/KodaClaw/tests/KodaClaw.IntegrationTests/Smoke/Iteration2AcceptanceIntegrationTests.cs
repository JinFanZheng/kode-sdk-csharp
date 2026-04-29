using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Settings;
using KodaClaw.ControlPlane;
using KodaClaw.IntegrationTests.Gateway;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Smoke;

public sealed class Iteration2AcceptanceIntegrationTests
{
    private const string GatewayToken = "test-token";

    [Fact]
    public async Task Iteration_2_acceptance_should_cover_control_plane_approval_sessions_diagnostics_and_settings()
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

        var runtime = hosted.Services.GetRequiredService<IMainSessionService>();
        var approvals = hosted.Services.GetRequiredService<IApprovalRepository>();
        var inbox = hosted.Services.GetRequiredService<IInboxRepository>();

        var handle = await runtime.EnsureMainSessionAsync();
        var runTask = handle.Agent.RunAsync("Please execute the dangerous tool.");

        var pendingApproval = await WaitForPendingApprovalAsync(approvals, handle.SessionId);
        pendingApproval.Status.Should().Be(ApprovalStatus.Pending);
        pendingApproval.SessionId.Should().Be(handle.SessionId);
        pendingApproval.InboxItemId.Should().NotBeNullOrWhiteSpace();

        var approvalPayload = JsonDocument.Parse(pendingApproval.PayloadJson!);
        var pendingCallId = approvalPayload.RootElement.GetProperty("callId").GetString();
        pendingCallId.Should().NotBeNullOrWhiteSpace();

        var approvalsResponse = await hosted.Client.GetAsync(
            $"/api/approvals?status=Pending&sessionId={Uri.EscapeDataString(handle.SessionId)}&limit=20");
        approvalsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var approvalsPayload = await approvalsResponse.Content.ReadFromJsonAsync<ApprovalQueryResponse>();
        approvalsPayload.Should().NotBeNull();
        approvalsPayload!.Items.Should().Contain(item => item.Id == pendingApproval.Id);

        var inboxResponse = await hosted.Client.GetAsync(
            $"/api/inbox?status=Open&sessionId={Uri.EscapeDataString(handle.SessionId)}&limit=20");
        inboxResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var inboxPayload = await inboxResponse.Content.ReadFromJsonAsync<InboxQueryResponse>();
        inboxPayload.Should().NotBeNull();
        inboxPayload!.Items.Should().Contain(item =>
            item.Id == pendingApproval.InboxItemId &&
            item.ApprovalId == pendingApproval.Id &&
            item.Status == InboxItemStatus.Open);

        var sessionDetailResponse = await hosted.Client.GetAsync($"/api/sessions/{handle.SessionId}");
        sessionDetailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var sessionDetail = await sessionDetailResponse.Content.ReadFromJsonAsync<SessionDetail>();
        sessionDetail.Should().NotBeNull();
        sessionDetail!.Status.PendingApprovalCount.Should().BeGreaterThanOrEqualTo(1);
        sessionDetail.PendingApprovalCallIds.Should().Contain(pendingCallId!);

        await WaitForDiagnosticsEventAsync(hosted.Client, handle.SessionId, "main_session.approval.requested");

        var approveResponse = await hosted.Client.PostAsJsonAsync(
            $"/api/approvals/{pendingApproval.Id}/approve",
            new ApprovalDecisionRequest("approved by iteration-2 acceptance"));
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var approved = await approveResponse.Content.ReadFromJsonAsync<Approval>();
        approved.Should().NotBeNull();
        approved!.Status.Should().Be(ApprovalStatus.Approved);

        var runResult = await runTask.WaitAsync(TimeSpan.FromSeconds(8));
        runResult.Success.Should().BeTrue();
        runResult.StopReason.Should().Be(StopReason.EndTurn);

        var resolvedInbox = await WaitForInboxStatusAsync(
            inbox,
            pendingApproval.InboxItemId!,
            InboxItemStatus.Resolved);
        resolvedInbox.ResolvedAt.Should().NotBeNull();

        var inboxDetailResponse = await hosted.Client.GetAsync($"/api/inbox/{pendingApproval.InboxItemId}");
        inboxDetailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var inboxDetail = await inboxDetailResponse.Content.ReadFromJsonAsync<InboxItem>();
        inboxDetail.Should().NotBeNull();
        inboxDetail!.Status.Should().Be(InboxItemStatus.Resolved);

        await WaitForDiagnosticsEventAsync(hosted.Client, handle.SessionId, "main_session.approval.decided");

        var settingsBefore = await hosted.Client.GetFromJsonAsync<KodaClawSettings>("/api/settings");
        settingsBefore.Should().NotBeNull();
        settingsBefore!.DefaultLandingRoute.Should().Be("/chat");

        var updateSettingsRequest = settingsBefore with
        {
            DefaultLandingRoute = "/inbox",
            Theme = ThemeMode.Dark,
            NotificationsEnabled = false,
            QuietHoursEnabled = true,
            QuietHoursStartLocalTime = "09:00",
            QuietHoursEndLocalTime = "18:00",
            UpdatedAt = DateTimeOffset.UnixEpoch
        };

        var updateSettingsResponse = await hosted.Client.PutAsJsonAsync("/api/settings", updateSettingsRequest);
        updateSettingsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updatedSettings = await updateSettingsResponse.Content.ReadFromJsonAsync<KodaClawSettings>();
        updatedSettings.Should().NotBeNull();
        updatedSettings!.DefaultLandingRoute.Should().Be("/inbox");
        updatedSettings.Theme.Should().Be(ThemeMode.Dark);
        updatedSettings.NotificationsEnabled.Should().BeFalse();
        updatedSettings.QuietHoursEnabled.Should().BeTrue();
        updatedSettings.QuietHoursStartLocalTime.Should().Be("09:00");
        updatedSettings.QuietHoursEndLocalTime.Should().Be("18:00");
        updatedSettings.UpdatedAt.Should().NotBe(DateTimeOffset.UnixEpoch);

        var fetchedSettings = await hosted.Client.GetFromJsonAsync<KodaClawSettings>("/api/settings");
        fetchedSettings.Should().Be(updatedSettings);
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
                services.AddSingleton<IModelProvider>(new ApprovalDrivenModelProvider());
                services.AddSingleton(new MainSessionOptions
                {
                    Model = "approval-model",
                    MaxIterations = 8,
                    Tools = ["danger_tool"],
                    Permissions = new PermissionConfig
                    {
                        Mode = "auto",
                        RequireApprovalTools = ["danger_tool"]
                    }
                });
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                    ["KODACLAW_DEFAULT_MODEL"] = "approval-model",
                    ["OPENAI_API_KEY"] = "stub-key"
                });
            },
            useTestWorkspaceService: false);
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

    private static Task<InboxItem> WaitForInboxStatusAsync(
        IInboxRepository inboxRepository,
        string inboxId,
        InboxItemStatus status)
    {
        return EventuallyAsync(async () =>
        {
            var item = await inboxRepository.GetByIdAsync(inboxId);
            return item?.Status == status ? item : null;
        }, $"Timed out waiting for inbox item '{inboxId}' to reach status '{status}'.");
    }

    private static Task<DiagnosticsQueryResponse> WaitForDiagnosticsEventAsync(
        HttpClient client,
        string sessionId,
        string eventType)
    {
        return EventuallyAsync(async () =>
        {
            var response = await client.GetAsync(
                $"/api/diagnostics/timeline?sessionId={Uri.EscapeDataString(sessionId)}&limit=100");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<DiagnosticsQueryResponse>();
            return payload?.Events.Any(item => item.EventType == eventType) == true
                ? payload
                : null;
        }, $"Timed out waiting for diagnostics event '{eventType}'.");
    }

    private static async Task<T> EventuallyAsync<T>(
        Func<Task<T?>> probe,
        string failureMessage,
        int attempts = 120,
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
                "kodaclaw-iteration2-acceptance",
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
                        Name = "danger_tool"
                    }
                };
                yield return new StreamChunk
                {
                    Type = StreamChunkType.ToolUseInputDelta,
                    ToolUse = new ToolUseChunk
                    {
                        Id = ApprovalCallId,
                        InputDelta = "{\"path\":\"/tmp/demo.txt\"}"
                    }
                };
                yield return new StreamChunk
                {
                    Type = StreamChunkType.ToolUseComplete,
                    ToolUse = new ToolUseChunk
                    {
                        Id = ApprovalCallId
                    }
                };
                yield return new StreamChunk
                {
                    Type = StreamChunkType.MessageStop,
                    StopReason = ModelStopReason.ToolUse,
                    Usage = new TokenUsage
                    {
                        InputTokens = 0,
                        OutputTokens = 0
                    }
                };
                yield break;
            }

            yield return new StreamChunk
            {
                Type = StreamChunkType.TextDelta,
                TextDelta = "approved"
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
            return Task.FromResult(new ModelResponse
            {
                Content =
                [
                    new TextContent
                    {
                        Text = "approved"
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
                path = new { type = "string" }
            },
            required = new[] { "path" }
        };

        public override ToolAttributes Attributes => new()
        {
            RequiresApproval = true
        };

        protected override Task<ToolResult> ExecuteAsync(
            DangerToolArgs arguments,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(ToolResult.Ok(new
            {
                status = "executed",
                path = arguments.Path
            }));
        }
    }

    private sealed class DangerToolArgs
    {
        public required string Path { get; init; }
    }
}
