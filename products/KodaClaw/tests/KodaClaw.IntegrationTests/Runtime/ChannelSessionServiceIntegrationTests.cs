using System.Linq;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Xunit;

namespace KodaClaw.IntegrationTests.Runtime;

public sealed class ChannelSessionServiceIntegrationTests
{
    [Fact]
    public async Task Ensure_channel_dm_session_creates_handle_and_loads_dm_context()
    {
        using var fixture = new ChannelRuntimeFixture();
        await fixture.PrepareWorkspaceContextAsync();
        await fixture.WriteThreadSummaryAsync("binding-dm-001", "Thread anchor: customer asked about deployment.");
        await using var service = fixture.CreateService();

        var binding = CreateBinding(
            bindingId: "binding-dm-001",
            sessionId: "channel-dm-binding-dm-001",
            threadType: ChannelThreadType.DirectMessage);
        var policy = CreatePolicy(ChannelThreadType.DirectMessage);

        var handle = await service.EnsureChannelSessionAsync(binding, policy);
        var result = await handle.Agent.RunAsync("inspect");

        result.Success.Should().BeTrue();
        handle.BindingId.Should().Be("binding-dm-001");
        handle.SessionId.Should().Be("channel-dm-binding-dm-001");
        handle.SessionKind.Should().Be(SessionKind.ChannelDirectMessage);
        Directory.Exists(handle.SessionDirectory).Should().BeTrue();
        handle.ResumedFromStore.Should().BeFalse();

        fixture.ModelProvider.LastRequest.Should().NotBeNull();
        var prompt = fixture.ModelProvider.LastRequest!.SystemPrompt;
        prompt.Should().Contain("Id: ChannelDirectMessage");
        prompt.Should().Contain("ThreadType: DirectMessage");
        prompt.Should().Contain("SessionKind: ChannelDirectMessage");
        prompt.Should().Contain("### File: workspace/AGENTS.md");
        prompt.Should().Contain("### File: workspace/IDENTITY.md");
        prompt.Should().Contain("### File: workspace/SOUL.md");
        prompt.Should().Contain("### File: workspace/USER.md");
        prompt.Should().Contain("### File: workspace/channels/binding-dm-001/SUMMARY.md");
        prompt.Should().Contain("User anchor: prefers concise replies.");
        prompt.Should().Contain("Thread anchor: customer asked about deployment.");
        prompt.Should().Contain("bounded delegate inside a private conversation");
        // KC-5001: DM sessions now load full workspace context (MEMORY.md included).
        prompt.Should().Contain("Memory anchor: do not leak this.");
        prompt.Should().Contain("### File: workspace/MEMORY.md");

        var promptReport = await SessionPromptReportStore.TryReadAsync(handle.SessionDirectory);
        promptReport.Should().NotBeNull();
        promptReport!.ProfileId.Should().Be("ChannelDirectMessage");
        promptReport.LoadedContextFiles.Should().Contain("workspace/USER.md");
        promptReport.LoadedContextFiles.Should().Contain("workspace/MEMORY.md");
        promptReport.LoadedContextFiles.Should().Contain("workspace/channels/binding-dm-001/SUMMARY.md");
    }

    [Fact]
    public async Task Ensure_channel_group_session_omits_user_profile_and_memory_context()
    {
        using var fixture = new ChannelRuntimeFixture();
        await fixture.PrepareWorkspaceContextAsync();
        await fixture.WriteThreadSummaryAsync("binding-group-001", "Thread anchor: group bridge is muted until mention.");
        await using var service = fixture.CreateService();

        var binding = CreateBinding(
            bindingId: "binding-group-001",
            sessionId: "channel-group-binding-group-001",
            threadType: ChannelThreadType.Group);
        var policy = CreatePolicy(ChannelThreadType.Group) with
        {
            LoadUserProfile = true,
            LoadLongTermMemory = true,
        };

        var handle = await service.EnsureChannelSessionAsync(binding, policy);
        var result = await handle.Agent.RunAsync("inspect");

        result.Success.Should().BeTrue();
        handle.SessionKind.Should().Be(SessionKind.ChannelGroup);

        fixture.ModelProvider.LastRequest.Should().NotBeNull();
        var prompt = fixture.ModelProvider.LastRequest!.SystemPrompt;
        prompt.Should().Contain("Id: ChannelGroup");
        prompt.Should().Contain("ThreadType: Group");
        prompt.Should().Contain("SessionKind: ChannelGroup");
        prompt.Should().Contain("### File: workspace/AGENTS.md");
        prompt.Should().Contain("### File: workspace/IDENTITY.md");
        prompt.Should().Contain("### File: workspace/SOUL.md");
        prompt.Should().Contain("### File: workspace/channels/binding-group-001/SUMMARY.md");
        prompt.Should().NotContain("### File: workspace/USER.md");
        prompt.Should().NotContain("### File: workspace/MEMORY.md");
        prompt.Should().NotContain("User anchor: prefers concise replies.");
        prompt.Should().NotContain("Memory anchor: do not leak this.");
        prompt.Should().Contain("prefer observing over replying unless Koda is explicitly mentioned");
    }

    [Fact]
    public async Task Run_inbound_turn_includes_group_guardrails_in_user_prompt()
    {
        using var fixture = new ChannelRuntimeFixture();
        await fixture.PrepareWorkspaceContextAsync();
        await using var service = fixture.CreateService();

        var binding = CreateBinding(
            bindingId: "binding-group-turn-001",
            sessionId: "channel-group-binding-group-turn-001",
            threadType: ChannelThreadType.Group);
        var policy = CreatePolicy(ChannelThreadType.Group);
        var envelope = new ChannelEventEnvelope(
            EventType: ChannelEventType.MessageReceived,
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: "telegram-main",
            ExternalThreadId: binding.ExternalThreadId,
            ThreadType: ChannelThreadType.Group,
            OccurredAt: DateTimeOffset.UtcNow,
            Sender: new ChannelIdentity("user-001", "alice", "Alice"),
            Recipient: new ChannelIdentity("koda", "koda_bot", "Koda"),
            Text: "What do we do next?",
            EventId: "event-group-turn-001",
            ExternalMessageId: "message-group-turn-001");

        var result = await service.RunInboundTurnAsync(binding, policy, envelope, hasExplicitMention: false);

        result.Proposal.Should().BeNull();
        fixture.ModelProvider.LastRequest.Should().NotBeNull();

        var requestText = string.Join(
            "\n",
            fixture.ModelProvider.LastRequest!.Messages
                .SelectMany(message => message.Content.OfType<TextContent>())
                .Select(content => content.Text));
        requestText.Should().Contain("HasExplicitMention: False");
        requestText.Should().Contain("This is a group thread without an explicit mention of Koda.");
        requestText.Should().Contain("Full Agent Mode");
    }

    [Fact]
    public async Task Ensure_channel_session_reuses_agent_for_same_binding_within_service()
    {
        using var fixture = new ChannelRuntimeFixture();
        await fixture.PrepareWorkspaceContextAsync();
        await using var service = fixture.CreateService();

        var binding = CreateBinding(
            bindingId: "binding-reuse-001",
            sessionId: "channel-dm-binding-reuse-001",
            threadType: ChannelThreadType.DirectMessage);
        var policy = CreatePolicy(ChannelThreadType.DirectMessage);

        var first = await service.EnsureChannelSessionAsync(binding, policy);
        var second = await service.EnsureChannelSessionAsync(binding, policy);

        second.SessionId.Should().Be(first.SessionId);
        ReferenceEquals(second.Agent, first.Agent).Should().BeTrue();
    }

    [Fact]
    public async Task Ensure_channel_session_resumes_from_store_across_service_instances()
    {
        using var fixture = new ChannelRuntimeFixture();
        await fixture.PrepareWorkspaceContextAsync();

        var binding = CreateBinding(
            bindingId: "binding-resume-001",
            sessionId: "channel-dm-binding-resume-001",
            threadType: ChannelThreadType.DirectMessage);
        var policy = CreatePolicy(ChannelThreadType.DirectMessage);

        ChannelSessionHandle created;
        await using (var initialService = fixture.CreateService())
        {
            created = await initialService.EnsureChannelSessionAsync(binding, policy);
        }

        await using var resumedService = fixture.CreateService();
        var resumed = await resumedService.EnsureChannelSessionAsync(binding, policy);

        resumed.SessionId.Should().Be(created.SessionId);
        resumed.SessionKind.Should().Be(SessionKind.ChannelDirectMessage);
        resumed.ResumedFromStore.Should().BeTrue();
        resumed.ResumeFailureMessage.Should().BeNull();
    }

    [Fact]
    public async Task Ensure_channel_session_falls_back_to_fresh_agent_on_resume_failure_without_rebinding_thread()
    {
        using var fixture = new ChannelRuntimeFixture();
        await fixture.PrepareWorkspaceContextAsync();

        var binding = CreateBinding(
            bindingId: "binding-fallback-001",
            sessionId: "channel-group-binding-fallback-001",
            threadType: ChannelThreadType.Group);
        var policy = CreatePolicy(ChannelThreadType.Group);

        ChannelSessionHandle created;
        await using (var initialService = fixture.CreateService())
        {
            created = await initialService.EnsureChannelSessionAsync(binding, policy);
        }

        fixture.CorruptMeta(created.SessionId);

        await using var recoveredService = fixture.CreateService();
        var recovered = await recoveredService.EnsureChannelSessionAsync(binding, policy);

        recovered.SessionId.Should().Be(created.SessionId);
        recovered.SessionKind.Should().Be(SessionKind.ChannelGroup);
        recovered.ResumedFromStore.Should().BeFalse();
        recovered.ResumeFailureMessage.Should().Contain("Resume failed");
    }

    private sealed class ChannelRuntimeFixture : IDisposable
    {
        public ChannelRuntimeFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-channel-runtime-tests", Guid.NewGuid().ToString("N"));
            Workspace = new WorkspaceService(new KodaClawWorkspaceOptions
            {
                RootPath = RootPath,
            });
            ModelProvider = new CapturingModelProvider();
        }

        public string RootPath { get; }

        public WorkspaceService Workspace { get; }

        public CapturingModelProvider ModelProvider { get; }

        public async Task PrepareWorkspaceContextAsync()
        {
            await Workspace.EnsureInitializedAsync();

            await WriteWorkspaceFileAsync(
                KodaClawWorkspaceLayout.AgentsFile,
                """
                # Agents

                - Agents anchor: stay inspectable.
                """);
            await WriteWorkspaceFileAsync(
                KodaClawWorkspaceLayout.IdentityFile,
                """
                # Identity

                - Identity anchor: Koda keeps local context bounded.
                """);
            await WriteWorkspaceFileAsync(
                KodaClawWorkspaceLayout.SoulFile,
                """
                # Soul

                - Soul anchor: calm, practical, direct.
                """);
            await WriteWorkspaceFileAsync(
                KodaClawWorkspaceLayout.UserFile,
                """
                # User

                - User anchor: prefers concise replies.
                """);
            await WriteWorkspaceFileAsync(
                KodaClawWorkspaceLayout.MemoryFile,
                """
                # Memory

                - Memory anchor: do not leak this.
                """);
        }

        public async Task WriteThreadSummaryAsync(string bindingId, string content)
        {
            var summaryPath = Path.Combine(
                RootPath,
                KodaClawWorkspaceLayout.WorkspaceDirectory,
                "channels",
                bindingId,
                "SUMMARY.md");
            Directory.CreateDirectory(Path.GetDirectoryName(summaryPath)!);
            await File.WriteAllTextAsync(summaryPath, content);
        }

        public void CorruptMeta(string sessionId)
        {
            var metaPath = Path.Combine(Workspace.GetSessionDirectory(sessionId), "meta.json");
            File.WriteAllText(metaPath, "{ invalid json");
        }

        public ChannelSessionService CreateService()
        {
            var dependencyFactory = new DefaultMainSessionAgentDependenciesFactory(new MainSessionDependencies
            {
                ModelProvider = ModelProvider,
            });

            return new ChannelSessionService(
                Workspace,
                dependencyFactory,
                new ChannelSessionOptions
                {
                    Model = "channel-capture-model",
                    MaxIterations = 4,
                });
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        private async Task WriteWorkspaceFileAsync(string fileName, string content)
        {
            var path = Path.Combine(
                RootPath,
                KodaClawWorkspaceLayout.WorkspaceDirectory,
                fileName);
            await File.WriteAllTextAsync(path, content);
        }
    }

    private sealed class CapturingModelProvider : IModelProvider
    {
        public string ProviderName => "capturing";

        public ModelRequest? LastRequest { get; private set; }
        public string ResponseText { get; set; } = "channel-ok";

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
                TextDelta = ResponseText,
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
                        Text = ResponseText,
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

    private static ThreadBinding CreateBinding(
        string bindingId,
        string sessionId,
        ChannelThreadType threadType)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionKind = threadType == ChannelThreadType.DirectMessage
            ? SessionKind.ChannelDirectMessage
            : SessionKind.ChannelGroup;

        return new ThreadBinding(
            Id: bindingId,
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: "telegram-main",
            ExternalThreadId: threadType == ChannelThreadType.DirectMessage ? "10001" : "-100200",
            ThreadType: threadType,
            SessionId: sessionId,
            SessionKind: sessionKind,
            ChannelIdentity: new ChannelIdentity(
                Id: threadType == ChannelThreadType.DirectMessage ? "user-001" : "group-001",
                Username: threadType == ChannelThreadType.DirectMessage ? "alice" : "ops_bridge",
                DisplayName: threadType == ChannelThreadType.DirectMessage ? "Alice" : "Ops Bridge"),
            PolicyId: threadType == ChannelThreadType.DirectMessage ? "policy-default-dm" : "policy-default-group",
            DeliveryRuleId: threadType == ChannelThreadType.DirectMessage ? "delivery-default-dm" : "delivery-default-group",
            CreatedAt: now,
            UpdatedAt: now,
            LastInboundAt: now,
            LastMessagePreview: "hello");
    }

    private static ChannelPolicy CreatePolicy(ChannelThreadType threadType)
    {
        var now = new DateTimeOffset(2026, 3, 19, 12, 0, 0, TimeSpan.Zero);
        return new ChannelPolicy(
            Id: threadType == ChannelThreadType.DirectMessage ? "policy-default-dm" : "policy-default-group",
            ThreadType: threadType,
            UpdatedAt: now,
            LoadAgents: true,
            LoadIdentity: true,
            LoadSoul: true,
            LoadUserProfile: threadType == ChannelThreadType.DirectMessage,
            LoadLongTermMemory: true,
            LoadRecentThreadSummary: true,
            AllowDirectReply: true,
            RequireExplicitMention: threadType == ChannelThreadType.Group);
    }
}
