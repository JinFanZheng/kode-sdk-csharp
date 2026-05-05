using System.Runtime.CompilerServices;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Chat;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Xunit;

namespace KodaClaw.IntegrationTests.Runtime;

/// <summary>
/// L2 integration tests for sub-agent progress visibility (KC-7101 / KC-7102).
/// Verifies that ChatSessionService forwards Monitor-channel sub-agent events
/// as SSE events: subagent_start, subagent_working, subagent_tool_done.
/// </summary>
public sealed class SubAgentProgressTests
{
    [Fact]
    public async Task ChatSessionService_should_emit_subagent_start_working_and_tool_done_events()
    {
        // Arrange
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new SubAgentEmitterTool());

        var modelProvider = new SubAgentToolModelProvider("subagent_emitter");

        using var fixture = new SubAgentProgressFixture(modelProvider, toolRegistry);
        await using var mainSessionService = fixture.CreateMainSessionService();
        var chatService = new ChatSessionService(mainSessionService);

        // Act
        var events = await CollectAsync(chatService.StreamMainSessionAsync(new ChatStreamRequest("test")));

        // Assert — core sub-agent events must be present
        events.Should().Contain(e => e.Type == "agent_working" && e.ToolName == "subagent_emitter",
            "agent_working should fire when the parent tool starts");

        events.Should().Contain(e => e.Type == "subagent_start" && e.SubAgentId != null && e.Label == "isolate_task",
            "subagent_start should carry SubAgentId and Label");

        events.Should().Contain(e => e.Type == "subagent_working" && e.SubAgentId != null && e.SubAgentToolName == "fs_read",
            "subagent_working should carry SubAgentId and tool name");

        events.Should().Contain(e => e.Type == "subagent_tool_done" && e.SubAgentId != null && e.SubAgentToolName == "fs_read",
            "subagent_tool_done should carry SubAgentId and tool name");

        events.Should().Contain(e => e.Type == "tool_activity" && e.ToolName == "subagent_emitter",
            "tool_activity should fire when the parent tool completes");

        events.Last().Type.Should().Be("done");
    }

    [Fact]
    public async Task SubAgentId_should_be_consistent_across_start_working_and_done_events()
    {
        // Arrange
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new SubAgentEmitterTool());

        var modelProvider = new SubAgentToolModelProvider("subagent_emitter");

        using var fixture = new SubAgentProgressFixture(modelProvider, toolRegistry);
        await using var mainSessionService = fixture.CreateMainSessionService();
        var chatService = new ChatSessionService(mainSessionService);

        // Act
        var events = await CollectAsync(chatService.StreamMainSessionAsync(new ChatStreamRequest("test")));

        // Assert — all sub-agent events should share the same SubAgentId
        var startId = events.FirstOrDefault(e => e.Type == "subagent_start")?.SubAgentId;
        var workingId = events.FirstOrDefault(e => e.Type == "subagent_working")?.SubAgentId;
        var doneId = events.FirstOrDefault(e => e.Type == "subagent_tool_done")?.SubAgentId;

        startId.Should().NotBeNullOrEmpty();
        workingId.Should().Be(startId);
        doneId.Should().Be(startId);
    }

    [Fact]
    public async Task Event_ordering_should_place_subagent_events_between_agent_working_and_tool_activity()
    {
        // Arrange
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new SubAgentEmitterTool());

        var modelProvider = new SubAgentToolModelProvider("subagent_emitter");

        using var fixture = new SubAgentProgressFixture(modelProvider, toolRegistry);
        await using var mainSessionService = fixture.CreateMainSessionService();
        var chatService = new ChatSessionService(mainSessionService);

        // Act
        var events = await CollectAsync(chatService.StreamMainSessionAsync(new ChatStreamRequest("test")));

        // Assert — index ordering
        var types = events.Select(e => e.Type).ToList();
        var idxAgentWorking = types.IndexOf("agent_working");
        var idxToolActivity = types.IndexOf("tool_activity");
        var idxSubStart = types.IndexOf("subagent_start");

        idxAgentWorking.Should().BeGreaterThanOrEqualTo(0);
        idxToolActivity.Should().BeGreaterThan(idxAgentWorking);
        idxSubStart.Should().BeGreaterThan(idxAgentWorking);
        idxSubStart.Should().BeLessThan(idxToolActivity);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<List<ChatStreamEvent>> CollectAsync(IAsyncEnumerable<ChatStreamEvent> source)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var item in source) events.Add(item);
        return events;
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    private sealed class SubAgentProgressFixture : IDisposable
    {
        public SubAgentProgressFixture(IModelProvider modelProvider, ToolRegistry toolRegistry)
        {
            RootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-subagent-tests", Guid.NewGuid().ToString("N"));
            Workspace = new FakeWorkspaceService(RootPath);
            DependencyFactory = new DefaultMainSessionAgentDependenciesFactory(new MainSessionDependencies
            {
                ModelProvider = modelProvider,
                ToolRegistry = toolRegistry,
            });
        }

        public string RootPath { get; }
        public FakeWorkspaceService Workspace { get; }
        public IMainSessionAgentDependenciesFactory DependencyFactory { get; }

        public MainSessionService CreateMainSessionService() =>
            new MainSessionService(
                Workspace,
                DependencyFactory,
                new MainSessionOptions
                {
                    Model = "stub-model",
                    MaxIterations = 4,
                    Tools = ["subagent_emitter"],
                });

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                try { Directory.Delete(RootPath, recursive: true); }
                catch (IOException) { }
            }
        }
    }

    // Minimal workspace fake — same as in ChatSessionServiceIntegrationTests
    private sealed class FakeWorkspaceService : IWorkspaceService
    {
        private WorkspaceAppConfig _config = new();
        private bool _initialized;
        public FakeWorkspaceService(string rootPath) { RootPath = rootPath; }
        public string RootPath { get; }
        public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceSnapshot(RootPath, _config.WorkspaceVersion, _initialized, !_config.BootstrapCompleted, _config.ActiveMainSessionId, "device-test"));
        public Task<WorkspaceSnapshot> EnsureInitializedAsync(CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(Path.Combine(RootPath, "sessions"));
            _initialized = true;
            return GetSnapshotAsync(cancellationToken);
        }
        public Task<WorkspaceAppConfig> LoadAppConfigAsync(CancellationToken cancellationToken = default) => Task.FromResult(_config);
        public Task SaveAppConfigAsync(WorkspaceAppConfig appConfig, CancellationToken cancellationToken = default) { _config = appConfig; return Task.CompletedTask; }
        public string GetSessionDirectory(string sessionId) => Path.Combine(RootPath, "sessions", sessionId);
        public IReadOnlyList<string> GetSkillsPaths() => [];
        public Task<WorkspaceMcpConfig> ReadMcpConfigAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkspaceMcpConfig());
        public Task SaveMcpConfigAsync(WorkspaceMcpConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<GatewayConfig> ReadGatewayConfigAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GatewayConfig());
        public Task SaveGatewayConfigAsync(GatewayConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TryCommitWorkspaceAsync(string message, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    // ── test tool: emits three Monitor events then returns ────────────────────

    /// <summary>
    /// A minimal tool that emits SubAgentCreatedEvent + SubAgentToolStartEvent + SubAgentToolEndEvent
    /// directly on the parent agent's event bus, simulating what SubAgentRunner does.
    /// </summary>
    private sealed class SubAgentEmitterTool : ToolBase
    {
        public override string Name => "subagent_emitter";
        public override string Description => "Test tool: emits sub-agent Monitor events.";
        public override object InputSchema => new { type = "object", properties = new { } };

        public override Task<ToolResult> ExecuteAsync(
            object arguments,
            ToolContext context,
            CancellationToken cancellationToken)
        {
            var bus = context.Agent?.EventBus;
            if (bus != null)
            {
                var subAgentId = "test-sub-" + Guid.NewGuid().ToString("N")[..12];
                const string label = "isolate_task";
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                bus.EmitMonitor(new SubAgentCreatedEvent
                {
                    Type = "subagent.created",
                    AgentId = subAgentId,
                    TemplateId = label,
                    ParentAgentId = "parent",
                    Timestamp = ts,
                });

                bus.EmitMonitor(new SubAgentToolStartEvent
                {
                    Type = "subagent.tool_start",
                    SubAgentId = subAgentId,
                    TemplateId = label,
                    ToolCallId = "tc-1",
                    ToolName = "fs_read",
                    Timestamp = ts,
                });

                bus.EmitMonitor(new SubAgentToolEndEvent
                {
                    Type = "subagent.tool_end",
                    SubAgentId = subAgentId,
                    TemplateId = label,
                    ToolCallId = "tc-1",
                    ToolName = "fs_read",
                    Timestamp = ts,
                });
            }

            return Task.FromResult(ToolResult.Ok("sub-agent completed"));
        }
    }

    // ── model provider: call tool once, then return text ─────────────────────

    private sealed class SubAgentToolModelProvider : IModelProvider
    {
        private readonly string _toolName;
        private int _callCount;

        public SubAgentToolModelProvider(string toolName)
        {
            _toolName = toolName;
        }

        public string ProviderName => "stub-subagent";

        public async IAsyncEnumerable<StreamChunk> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();

            var call = System.Threading.Interlocked.Increment(ref _callCount);

            if (call == 1)
            {
                // First call: return a tool use request
                var toolCallId = "call-" + Guid.NewGuid().ToString("N")[..8];
                yield return new StreamChunk
                {
                    Type = StreamChunkType.ToolUseStart,
                    ToolUse = new ToolUseChunk { Id = toolCallId, Name = _toolName },
                };
                yield return new StreamChunk
                {
                    Type = StreamChunkType.ToolUseComplete,
                    ToolUse = new ToolUseChunk { Id = toolCallId, Input = new { } },
                };
                yield return new StreamChunk
                {
                    Type = StreamChunkType.MessageStop,
                    StopReason = ModelStopReason.ToolUse,
                    Usage = new TokenUsage { InputTokens = 0, OutputTokens = 0 },
                };
            }
            else
            {
                // Subsequent calls: return text response
                yield return new StreamChunk
                {
                    Type = StreamChunkType.TextDelta,
                    TextDelta = "completed",
                };
                yield return new StreamChunk
                {
                    Type = StreamChunkType.MessageStop,
                    StopReason = ModelStopReason.EndTurn,
                    Usage = new TokenUsage { InputTokens = 0, OutputTokens = 0 },
                };
            }
        }

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelResponse
            {
                Content = [new TextContent { Text = "completed" }],
                StopReason = ModelStopReason.EndTurn,
                Usage = new TokenUsage { InputTokens = 0, OutputTokens = 0 },
                Model = request.Model,
            });

        public Task<bool> ValidateAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public ModelCapabilities? GetModelCapabilities(string modelId) => null;
    }
}
