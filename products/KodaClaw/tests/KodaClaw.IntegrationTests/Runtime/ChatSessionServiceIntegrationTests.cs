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
using Kode.Agent.Store.Json;
using Xunit;

namespace KodaClaw.IntegrationTests.Runtime;

public sealed class ChatSessionServiceIntegrationTests
{
    [Fact]
    public async Task Stream_main_session_should_emit_text_chunk_then_done()
    {
        using var fixture = new RuntimeFixture(new StubModelProvider());
        await using var mainSessionService = fixture.CreateMainSessionService();
        var chatService = new ChatSessionService(mainSessionService);

        var events = await CollectAsync(chatService.StreamMainSessionAsync(new ChatStreamRequest("hello")));

        events.Select(item => item.Type).Should().ContainInOrder("text_chunk", "done");
        events[0].Delta.Should().Be("stub");
        events[0].Sequence.Should().NotBeNull();
        events[0].Timestamp.Should().NotBeNull();
        events[1].Reason.Should().NotBeNullOrWhiteSpace();
        events.Select(item => item.SessionId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Stream_main_session_should_persist_ready_breakpoint_after_completion()
    {
        using var fixture = new RuntimeFixture(new StubModelProvider());
        await using var mainSessionService = fixture.CreateMainSessionService();
        var chatService = new ChatSessionService(mainSessionService);

        var events = await CollectAsync(chatService.StreamMainSessionAsync(new ChatStreamRequest("persist ready")));
        var sessionId = events.Select(item => item.SessionId).Distinct().Single();

        AgentInfo? info = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            info = await fixture.LoadPersistedInfoAsync(sessionId);
            if (info?.Breakpoint == BreakpointState.Ready)
            {
                break;
            }

            await Task.Delay(50);
        }

        info.Should().NotBeNull();
        info!.Breakpoint.Should().Be(BreakpointState.Ready);

        var promptReport = await SessionPromptReportStore.TryReadAsync(
            fixture.Workspace.GetSessionDirectory(sessionId));
        promptReport.Should().NotBeNull();
        promptReport!.ProfileId.Should().Be("Main");
        promptReport.SystemPrompt.Should().Contain("Id: Main");
    }

    [Fact]
    public async Task Stream_main_session_should_emit_error_event_when_runtime_fails()
    {
        using var fixture = new RuntimeFixture(new FailingModelProvider("simulated runtime failure"));
        await using var mainSessionService = fixture.CreateMainSessionService();
        var chatService = new ChatSessionService(mainSessionService);

        var events = await CollectAsync(chatService.StreamMainSessionAsync(new ChatStreamRequest("hello")));

        events.Should().ContainSingle();
        events[0].Type.Should().Be("error");
        events[0].Error.Should().NotBeNull();
        events[0].Error!.Code.Should().Be("runtime.error");
        events[0].Error!.Message.Should().Contain("simulated runtime failure");
    }

    [Fact]
    public async Task Stream_main_session_should_recover_when_previous_store_metadata_is_invalid()
    {
        using var fixture = new RuntimeFixture(new StubModelProvider());
        string originalSessionId;

        await using (var mainSessionService = fixture.CreateMainSessionService())
        {
            var chatService = new ChatSessionService(mainSessionService);
            var events = await CollectAsync(chatService.StreamMainSessionAsync(new ChatStreamRequest("hello fallback")));

            events.Select(item => item.Type).Should().ContainInOrder("text_chunk", "done");
            originalSessionId = events.Select(item => item.SessionId).Distinct().Single();
        }

        fixture.CorruptMeta(originalSessionId);

        await using var recoveredMainSession = fixture.CreateMainSessionService();
        var recoveredChatService = new ChatSessionService(recoveredMainSession);
        var recoveredEvents = await CollectAsync(recoveredChatService.StreamMainSessionAsync(new ChatStreamRequest("after fallback")));

        recoveredEvents.Select(item => item.Type).Should().ContainInOrder("text_chunk", "done");
        var recoveredSessionId = recoveredEvents.Select(item => item.SessionId).Distinct().Single();
        recoveredSessionId.Should().NotBe(originalSessionId);

        var appConfig = await fixture.Workspace.LoadAppConfigAsync();
        appConfig.ActiveMainSessionId.Should().Be(recoveredSessionId);
    }

    [Fact]
    public async Task Stream_main_session_should_emit_session_rotated_when_workspace_rotation_pending()
    {
        using var fixture = new RuntimeFixture(new StubModelProvider());
        await using var mainSessionService = fixture.CreateMainSessionService();
        var chatService = new ChatSessionService(mainSessionService);

        // First normal turn establishes a session
        var firstEvents = await CollectAsync(chatService.StreamMainSessionAsync(new ChatStreamRequest("hello")));
        firstEvents.Select(e => e.Type).Should().ContainInOrder("text_chunk", "done");
        var firstSessionId = firstEvents.Last(e => e.SessionId != null).SessionId;

        // Simulate workspace_protocol_update triggering a rotation request
        mainSessionService.RequestWorkspaceRotation();

        // Next turn must start with session_rotated event
        var secondEvents = await CollectAsync(chatService.StreamMainSessionAsync(new ChatStreamRequest("after rotation")));
        secondEvents[0].Type.Should().Be("session_rotated");
        secondEvents[0].Reason.Should().Be("workspace_updated");
        secondEvents.Select(e => e.Type).Should().ContainInOrder("session_rotated", "text_chunk", "done");

        // Session ID must have changed
        var secondSessionId = secondEvents.Last(e => e.SessionId != null).SessionId;
        secondSessionId.Should().NotBe(firstSessionId);
    }

    [Fact]
    public async Task Stream_main_session_should_not_emit_session_rotated_on_normal_turn()
    {
        using var fixture = new RuntimeFixture(new StubModelProvider());
        await using var mainSessionService = fixture.CreateMainSessionService();
        var chatService = new ChatSessionService(mainSessionService);

        var events = await CollectAsync(chatService.StreamMainSessionAsync(new ChatStreamRequest("hello")));

        events.Should().NotContain(e => e.Type == "session_rotated");
        events.Select(e => e.Type).Should().ContainInOrder("text_chunk", "done");
    }

    private static async Task<List<ChatStreamEvent>> CollectAsync(IAsyncEnumerable<ChatStreamEvent> source)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var item in source)
        {
            events.Add(item);
        }

        return events;
    }

    private sealed class RuntimeFixture : IDisposable
    {
        public RuntimeFixture(IModelProvider modelProvider)
        {
            RootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-chat-tests", Guid.NewGuid().ToString("N"));
            Workspace = new FakeWorkspaceService(RootPath);
            DependencyFactory = new DefaultMainSessionAgentDependenciesFactory(new MainSessionDependencies
            {
                ModelProvider = modelProvider,
            });
        }

        public string RootPath { get; }

        public FakeWorkspaceService Workspace { get; }

        public IMainSessionAgentDependenciesFactory DependencyFactory { get; }

        public MainSessionService CreateMainSessionService()
        {
            return new MainSessionService(
                Workspace,
                DependencyFactory,
                new MainSessionOptions
                {
                    Model = "stub-model",
                    MaxIterations = 4,
                });
        }

        public Task<AgentInfo?> LoadPersistedInfoAsync(string sessionId)
        {
            var store = new JsonAgentStore(Path.Combine(RootPath, "sessions"));
            return store.LoadInfoAsync(sessionId);
        }

        public void Dispose()
        {
            DeleteDirectoryWithRetry(RootPath);
        }

        public void CorruptMeta(string sessionId)
        {
            var metaPath = Path.Combine(Workspace.GetSessionDirectory(sessionId), "meta.json");
            File.WriteAllText(metaPath, "{");
        }

        private static void DeleteDirectoryWithRetry(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            const int maxAttempts = 5;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(50 * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(50 * attempt);
                }
            }
        }
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

        public async IAsyncEnumerable<StreamChunk> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();

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

        public ModelCapabilities? GetModelCapabilities(string modelId) => null;
    }

    private sealed class FailingModelProvider : IModelProvider
    {
        private readonly string _message;

        public FailingModelProvider(string message)
        {
            _message = message;
        }

        public string ProviderName => "failing";

        public IAsyncEnumerable<StreamChunk> StreamAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            return Fail(cancellationToken);
        }

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(_message);
        }

        public Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public ModelCapabilities? GetModelCapabilities(string modelId) => null;

        private async IAsyncEnumerable<StreamChunk> Fail(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            throw new InvalidOperationException(_message);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

    }
}
