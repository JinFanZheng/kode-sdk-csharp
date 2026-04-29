using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.ControlPlane;
using KodaClaw.Storage.Json.Repositories;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Xunit;

namespace KodaClaw.IntegrationTests.Runtime;

public sealed class MainSessionServiceIntegrationTests
{
    [Fact]
    public async Task Ensure_main_session_creates_active_session_and_meta_file()
    {
        using var fixture = new RuntimeFixture();
        await using var service = fixture.CreateService();

        var handle = await service.EnsureMainSessionAsync();

        handle.SessionKind.Should().Be(SessionKind.Main);
        handle.SessionId.Should().NotBeNullOrWhiteSpace();
        handle.SessionDirectory.Should().Be(fixture.Workspace.GetSessionDirectory(handle.SessionId));
        handle.ResumedFromStore.Should().BeFalse();
        handle.ResumeFailureMessage.Should().BeNull();

        var appConfig = await fixture.Workspace.LoadAppConfigAsync();
        appConfig.ActiveMainSessionId.Should().Be(handle.SessionId);

        var metaPath = Path.Combine(handle.SessionDirectory, "meta.json");
        File.Exists(metaPath).Should().BeTrue();
    }

    [Fact]
    public async Task Ensure_main_session_reuses_active_session_on_repeat_call()
    {
        using var fixture = new RuntimeFixture();
        await using var service = fixture.CreateService();

        var first = await service.EnsureMainSessionAsync();
        var second = await service.EnsureMainSessionAsync();

        second.SessionId.Should().Be(first.SessionId);
        ReferenceEquals(second.Agent, first.Agent).Should().BeTrue();
    }

    [Fact]
    public async Task Ensure_main_session_resumes_from_store_across_service_instances()
    {
        using var fixture = new RuntimeFixture();
        MainSessionHandle created;
        await using (var service = fixture.CreateService())
        {
            created = await service.EnsureMainSessionAsync();
        }

        await using var resumedService = fixture.CreateService();
        var resumed = await resumedService.EnsureMainSessionAsync();

        resumed.SessionId.Should().Be(created.SessionId);
        resumed.ResumedFromStore.Should().BeTrue();
        resumed.ResumeFailureMessage.Should().BeNull();
    }

    [Fact]
    public async Task Ensure_main_session_records_creation_diagnostic()
    {
        using var fixture = new RuntimeFixture();
        fixture.CorrelationAccessor.CorrelationId = "create-correlation";

        await using var service = fixture.CreateService();
        var handle = await service.EnsureMainSessionAsync();

        var creationEvent = fixture.Diagnostics.GetRecent(limit: 5)
            .Single(e => e.EventType == "main_session.created");

        creationEvent.SessionId.Should().Be(handle.SessionId);
        creationEvent.CorrelationId.Should().Be("create-correlation");
        creationEvent.Level.Should().Be("info");
    }

    [Fact]
    public async Task Ensure_main_session_records_resume_diagnostic()
    {
        using var fixture = new RuntimeFixture();
        fixture.CorrelationAccessor.CorrelationId = "create-correlation";

        await using (var service = fixture.CreateService())
        {
            await service.EnsureMainSessionAsync();
        }

        fixture.CorrelationAccessor.CorrelationId = "resume-correlation";
        await using var resumedService = fixture.CreateService();
        var resumed = await resumedService.EnsureMainSessionAsync();

        var resumeEvent = fixture.Diagnostics.GetRecent(limit: 5, correlationId: "resume-correlation")
            .Single(e => e.EventType == "main_session.resumed");

        resumeEvent.SessionId.Should().Be(resumed.SessionId);
        resumeEvent.CorrelationId.Should().Be("resume-correlation");
        resumeEvent.Level.Should().Be("info");
    }

    [Fact]
    public async Task Ensure_main_session_falls_back_to_fresh_session_when_resume_from_store_fails()
    {
        using var fixture = new RuntimeFixture();
        MainSessionHandle created;
        await using (var service = fixture.CreateService())
        {
            created = await service.EnsureMainSessionAsync();
        }

        fixture.CorruptMeta(created.SessionId);

        fixture.CorrelationAccessor.CorrelationId = "fallback-correlation";
        await using var recoveredService = fixture.CreateService();
        var recovered = await recoveredService.EnsureMainSessionAsync();

        recovered.SessionId.Should().NotBe(created.SessionId);
        recovered.ResumedFromStore.Should().BeFalse();
        recovered.ResumeFailureMessage.Should().Contain("Resume failed");

        var fallbackEvent = fixture.Diagnostics.GetRecent(limit: 5, correlationId: "fallback-correlation")
            .Single(e => e.EventType == "main_session.resume_fallback");

        fallbackEvent.SessionId.Should().Be(recovered.SessionId);
        fallbackEvent.Level.Should().Be("warning");
        fallbackEvent.Message.Should().Contain("Resume failed");

        var appConfig = await fixture.Workspace.LoadAppConfigAsync();
        appConfig.ActiveMainSessionId.Should().Be(recovered.SessionId);
    }

    [Fact]
    public async Task Ensure_main_session_persists_runtime_approval_and_resolves_it_after_live_approval()
    {
        await using var fixture = new ApprovalRuntimeFixture();
        fixture.CorrelationAccessor.CorrelationId = "approval-correlation";

        await using var service = fixture.CreateService(new ApprovalDrivenModelProvider());
        var handle = await service.EnsureMainSessionAsync();

        var runTask = handle.Agent.RunAsync("Please execute the dangerous tool.");

        var approval = await fixture.WaitForApprovalAsync(handle.SessionId);
        approval.Status.Should().Be(ApprovalStatus.Pending);
        approval.CorrelationId.Should().Be("approval-correlation");
        approval.InboxItemId.Should().NotBeNullOrWhiteSpace();
        approval.PayloadJson.Should().Contain("\"toolName\":\"danger_tool\"");

        var inboxItem = await fixture.WaitForInboxItemAsync(approval.InboxItemId!);
        inboxItem.Status.Should().Be(InboxItemStatus.Open);
        inboxItem.RequiresAction.Should().BeTrue();
        inboxItem.ApprovalId.Should().Be(approval.Id);
        inboxItem.Route.Should().Be($"/approvals/{approval.Id}");

        var payload = JsonDocument.Parse(approval.PayloadJson!);
        var callId = payload.RootElement.GetProperty("callId").GetString();
        callId.Should().NotBeNullOrWhiteSpace();

        await handle.Agent.ApproveToolCallAsync(callId!);

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        result.Success.Should().BeTrue();
        result.StopReason.Should().Be(StopReason.EndTurn);
        result.Response.Should().Be("approved");

        var decidedApproval = await fixture.WaitForApprovalStateAsync(approval.Id, ApprovalStatus.Approved);
        decidedApproval.DecidedBy.Should().Be("api");

        var resolvedInboxItem = await fixture.WaitForInboxStatusAsync(inboxItem.Id, InboxItemStatus.Resolved);
        resolvedInboxItem.ResolvedAt.Should().NotBeNull();

        fixture.Diagnostics.Query(new DiagnosticsQuery(SessionId: handle.SessionId, Limit: 20))
            .Select(item => item.EventType)
            .Should()
            .Contain(new[]
            {
                "main_session.approval.requested",
                "main_session.approval.decided",
            });
    }

    [Fact]
    public async Task Ensure_main_session_cancels_stale_pending_approvals_after_resume()
    {
        await using var fixture = new ApprovalRuntimeFixture();
        string sessionId;

        await using (var initialService = fixture.CreateService(new StubModelProvider()))
        {
            var handle = await initialService.EnsureMainSessionAsync();
            sessionId = handle.SessionId;
        }

        var now = new DateTimeOffset(2026, 3, 18, 12, 0, 0, TimeSpan.Zero);
        var approval = new Approval(
            Id: "approval-stale-001",
            Kind: ApprovalKind.ExternalAction,
            Status: ApprovalStatus.Pending,
            Title: "Approve stale dangerous tool",
            Summary: "This approval should be canceled on resume.",
            Source: "runtime.main_session.approval",
            RequestedAt: now,
            UpdatedAt: now,
            SessionId: sessionId,
            CorrelationId: "stale-correlation",
            InboxItemId: "inbox-stale-001",
            PayloadJson: "{\"callId\":\"call-stale-001\"}");
        await fixture.Approvals.UpsertAsync(approval);
        await fixture.Inbox.UpsertAsync(new InboxItem(
            Id: "inbox-stale-001",
            Kind: InboxItemKind.Approval,
            Status: InboxItemStatus.Open,
            Title: "Approve stale dangerous tool",
            Summary: "This approval should be canceled on resume.",
            Source: "runtime.main_session.approval",
            CreatedAt: now,
            UpdatedAt: now,
            RequiresAction: true,
            Route: "/approvals/approval-stale-001",
            SessionId: sessionId,
            CorrelationId: "stale-correlation",
            ApprovalId: approval.Id));

        fixture.CorrelationAccessor.CorrelationId = "resume-correlation";
        await using var resumedService = fixture.CreateService(new StubModelProvider());
        var resumed = await resumedService.EnsureMainSessionAsync();

        resumed.SessionId.Should().Be(sessionId);
        resumed.ResumedFromStore.Should().BeTrue();

        var canceledApproval = await fixture.WaitForApprovalStateAsync(approval.Id, ApprovalStatus.Canceled);
        canceledApproval.DecisionNote.Should().Contain("Canceled stale approvals");
        canceledApproval.DecidedBy.Should().Be("runtime.resume");

        var resolvedInboxItem = await fixture.WaitForInboxStatusAsync("inbox-stale-001", InboxItemStatus.Resolved);
        resolvedInboxItem.ResolvedAt.Should().NotBeNull();

        fixture.Diagnostics.Query(new DiagnosticsQuery(SessionId: sessionId, Limit: 20))
            .Should()
            .Contain(item => item.EventType == "main_session.approval.stale_canceled");
    }

    private sealed class RuntimeFixture : IDisposable
    {
        public RuntimeFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-runtime-tests", Guid.NewGuid().ToString("N"));
            Workspace = new FakeWorkspaceService(RootPath);
        }

        public string RootPath { get; }

        public FakeWorkspaceService Workspace { get; }

        public TestDiagnosticsService Diagnostics { get; } = new();

        public TestCorrelationContextAccessor CorrelationAccessor { get; } = new();

        public MainSessionService CreateService()
        {
            var dependencyFactory = new DefaultMainSessionAgentDependenciesFactory(new MainSessionDependencies
            {
                ModelProvider = new StubModelProvider(),
            });

            return new MainSessionService(
                Workspace,
                dependencyFactory,
                new MainSessionOptions
                {
                    Model = "stub-model",
                    MaxIterations = 4,
                },
                Diagnostics,
                CorrelationAccessor);
        }

        public void CorruptMeta(string sessionId)
        {
            var metaPath = Path.Combine(Workspace.GetSessionDirectory(sessionId), "meta.json");
            File.WriteAllText(metaPath, "{");
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class TestDiagnosticsService : IDiagnosticsService
    {
        private readonly object _gate = new();
        private readonly List<DiagnosticEvent> _events = [];

        public void Record(DiagnosticEvent diagnosticEvent)
        {
            ArgumentNullException.ThrowIfNull(diagnosticEvent);

            lock (_gate)
            {
                _events.Add(diagnosticEvent);
            }
        }

        public IReadOnlyList<DiagnosticEvent> GetRecent(int limit = 50, string? correlationId = null)
        {
            if (limit <= 0)
            {
                return [];
            }

            lock (_gate)
            {
                IEnumerable<DiagnosticEvent> query = _events;
                if (!string.IsNullOrWhiteSpace(correlationId))
                {
                    query = query.Where(item => string.Equals(item.CorrelationId, correlationId, StringComparison.Ordinal));
                }

                return query
                    .OrderByDescending(item => item.Timestamp)
                    .Take(limit)
                    .ToArray();
            }
        }

        public IReadOnlyList<DiagnosticEvent> Query(DiagnosticsQuery? query = null)
        {
            var effective = query ?? new DiagnosticsQuery();
            if (effective.Limit <= 0)
            {
                return [];
            }

            lock (_gate)
            {
                IEnumerable<DiagnosticEvent> filtered = _events;
                if (!string.IsNullOrWhiteSpace(effective.CorrelationId))
                {
                    filtered = filtered.Where(item =>
                        string.Equals(item.CorrelationId, effective.CorrelationId, StringComparison.Ordinal));
                }

                if (!string.IsNullOrWhiteSpace(effective.SessionId))
                {
                    filtered = filtered.Where(item =>
                        string.Equals(item.SessionId, effective.SessionId, StringComparison.Ordinal));
                }

                if (!string.IsNullOrWhiteSpace(effective.Source))
                {
                    filtered = filtered.Where(item =>
                        string.Equals(item.Source, effective.Source, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(effective.EventType))
                {
                    filtered = filtered.Where(item =>
                        string.Equals(item.EventType, effective.EventType, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(effective.Level))
                {
                    filtered = filtered.Where(item =>
                        string.Equals(item.Level, effective.Level, StringComparison.OrdinalIgnoreCase));
                }

                return filtered
                    .OrderByDescending(item => item.Timestamp)
                    .Take(effective.Limit)
                    .ToArray();
            }
        }

        public DiagnosticsStatsResponse GetStats(DateTimeOffset? since = null) =>
            new(0, 0, 0, [], null, null);

        public Task ClearAsync(DateTimeOffset? before = null, CancellationToken cancellationToken = default)
        {
            lock (_gate) { _events.Clear(); }
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<DiagnosticEvent> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestCorrelationContextAccessor : ICorrelationContextAccessor
    {
        public string? CorrelationId { get; set; }
    }

    private sealed class FakeWorkspaceService : IWorkspaceService
    {
        private WorkspaceAppConfig _config = new();
        private bool _initialized;

        public FakeWorkspaceService(string rootPath)
        {
            RootPath = rootPath;
        }

        public string RootPath { get; }

        public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WorkspaceSnapshot(
                RootPath,
                _config.WorkspaceVersion,
                _initialized,
                !_config.BootstrapCompleted,
                _config.ActiveMainSessionId,
                "device-test"));
        }

        public Task<WorkspaceSnapshot> EnsureInitializedAsync(CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(Path.Combine(RootPath, "sessions"));
            _initialized = true;
            return GetSnapshotAsync(cancellationToken);
        }

        public Task<WorkspaceAppConfig> LoadAppConfigAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_config);
        }

        public Task SaveAppConfigAsync(WorkspaceAppConfig appConfig, CancellationToken cancellationToken = default)
        {
            _config = appConfig;
            return Task.CompletedTask;
        }

        public string GetSessionDirectory(string sessionId)
        {
            return Path.Combine(RootPath, "sessions", sessionId);
        }

        public IReadOnlyList<string> GetSkillsPaths() => [];

        public Task<WorkspaceMcpConfig> ReadMcpConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceMcpConfig());

        public Task SaveMcpConfigAsync(WorkspaceMcpConfig config, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<GatewayConfig> ReadGatewayConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GatewayConfig());

        public Task SaveGatewayConfigAsync(GatewayConfig config, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCommitWorkspaceAsync(string message, CancellationToken cancellationToken = default) => Task.FromResult(false);

    }

    private sealed class StubModelProvider : IModelProvider
    {
        public string ProviderName => "stub";

#pragma warning disable CS1998
        public async IAsyncEnumerable<StreamChunk> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new StreamChunk
            {
                Type = StreamChunkType.TextDelta,
                TextDelta = "stub",
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
#pragma warning restore CS1998

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelResponse
            {
                Content =
                [
                    new TextContent
                    {
                        Text = "stub",
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

    private sealed class ApprovalDrivenModelProvider : IModelProvider
    {
        private const string ApprovalCallId = "call-danger-001";

        public string ProviderName => "approval-stub";

#pragma warning disable CS1998
        public async IAsyncEnumerable<StreamChunk> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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
#pragma warning restore CS1998

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelResponse
            {
                Content =
                [
                    new TextContent
                    {
                        Text = "approved",
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

    private sealed class ApprovalRuntimeFixture : IAsyncDisposable
    {
        public ApprovalRuntimeFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-runtime-approval-tests", Guid.NewGuid().ToString("N"));
            Workspace = new WorkspaceService(new KodaClawWorkspaceOptions
            {
                RootPath = RootPath,
            });
            Diagnostics = new TestDiagnosticsService();
            CorrelationAccessor = new TestCorrelationContextAccessor();
            Approvals = new JsonApprovalRepository(RootPath);
            Inbox = new JsonInboxRepository(RootPath);
        }

        public string RootPath { get; }

        public WorkspaceService Workspace { get; }

        public TestDiagnosticsService Diagnostics { get; }

        public TestCorrelationContextAccessor CorrelationAccessor { get; }

        public JsonApprovalRepository Approvals { get; }

        public JsonInboxRepository Inbox { get; }

        public MainSessionService CreateService(IModelProvider modelProvider)
        {
            var registry = new ToolRegistry();
            registry.Register(new DangerTool());

            var dependencyFactory = new DefaultMainSessionAgentDependenciesFactory(new MainSessionDependencies
            {
                ModelProvider = modelProvider,
                ToolRegistry = registry,
            });

            return new MainSessionService(
                Workspace,
                dependencyFactory,
                new MainSessionOptions
                {
                    Model = "approval-model",
                    MaxIterations = 8,
                    Tools = ["danger_tool"],
                    Permissions = new PermissionConfig
                    {
                        Mode = "auto",
                        RequireApprovalTools = ["danger_tool"],
                    },
                },
                Diagnostics,
                CorrelationAccessor,
                Approvals,
                Inbox);
        }

        public async Task<Approval> WaitForApprovalAsync(string sessionId)
        {
            return await EventuallyAsync(async () =>
            {
                var pending = await Approvals.ListAsync(new ApprovalQuery(
                    Status: ApprovalStatus.Pending,
                    SessionId: sessionId,
                    Limit: 10));
                return pending.SingleOrDefault();
            }, "Timed out waiting for pending approval.");
        }

        public async Task<Approval> WaitForApprovalStateAsync(string approvalId, ApprovalStatus status)
        {
            return await EventuallyAsync(async () =>
            {
                var approval = await Approvals.GetByIdAsync(approvalId);
                return approval?.Status == status ? approval : null;
            }, $"Timed out waiting for approval '{approvalId}' to reach status '{status}'.");
        }

        public async Task<InboxItem> WaitForInboxItemAsync(string inboxItemId)
        {
            return await EventuallyAsync(async () => await Inbox.GetByIdAsync(inboxItemId), "Timed out waiting for inbox item.");
        }

        public async Task<InboxItem> WaitForInboxStatusAsync(string inboxItemId, InboxItemStatus status)
        {
            return await EventuallyAsync(async () =>
            {
                var item = await Inbox.GetByIdAsync(inboxItemId);
                return item?.Status == status ? item : null;
            }, $"Timed out waiting for inbox item '{inboxItemId}' to reach status '{status}'.");
        }

        public async ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }

            await Task.CompletedTask;
        }
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
            var value = await probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(delayMs);
        }

        throw new Xunit.Sdk.XunitException(failureMessage);
    }
}
