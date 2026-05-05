using System.Linq;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using AutomationDefinitionContract = KodaClaw.Contracts.Automations.AutomationDefinition;
using Xunit;

namespace KodaClaw.IntegrationTests.Runtime;

public sealed class AutomationSessionServiceIntegrationTests
{
    [Fact]
    public async Task Start_automation_session_creates_handle_with_automation_kind_and_ids()
    {
        using var fixture = new AutomationRuntimeFixture();
        await using var service = fixture.CreateService();

        var definition = CreateDefinition(
            id: "auto-daily-ops",
            title: "Daily Ops",
            prompt: "Check workspace health and produce a concise summary.");

        var handle = await service.StartAutomationSessionAsync(definition);

        handle.SessionKind.Should().Be(SessionKind.Automation);
        handle.AutomationId.Should().Be(definition.Id);
        handle.SessionId.Should().NotBeNullOrWhiteSpace();
        handle.SessionDirectory.Should().Be(fixture.Workspace.GetSessionDirectory(handle.SessionId));
        Directory.Exists(handle.SessionDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task Start_automation_session_injects_definition_prompt_and_workspace_context_into_model_request()
    {
        using var fixture = new AutomationRuntimeFixture();
        await fixture.PrepareWorkspaceContextAsync();
        await using var service = fixture.CreateService();

        var definition = CreateDefinition(
            id: "auto-heartbeat-review",
            title: "Heartbeat Review",
            prompt: "Summarize today's priorities from heartbeat and notes.",
            inputPaths:
            [
                "tasks/daily-note.md",
            ]);

        var handle = await service.StartAutomationSessionAsync(definition);
        var runResult = await handle.Agent.RunAsync("run");
        runResult.Success.Should().BeTrue();

        var request = fixture.ModelProvider.LastRequest;
        request.Should().NotBeNull();
        request!.SystemPrompt.Should().Contain("Summarize today's priorities from heartbeat and notes.");
        request.SystemPrompt.Should().Contain("Id: Automation");
        request.SystemPrompt.Should().Contain("Heartbeat anchor: review QA and send noon digest.");
        request.SystemPrompt.Should().Contain("Daily note anchor: collect incident digests.");
        request.SystemPrompt.Should().Contain("### File: workspace/HEARTBEAT.md");
        request.SystemPrompt.Should().Contain("### File: workspace/tasks/daily-note.md");

        var promptReport = await SessionPromptReportStore.TryReadAsync(handle.SessionDirectory);
        promptReport.Should().NotBeNull();
        promptReport!.ProfileId.Should().Be("Automation");
        promptReport.LoadedContextFiles.Should().Contain("workspace/HEARTBEAT.md");
        promptReport.LoadedContextFiles.Should().Contain("workspace/tasks/daily-note.md");
    }

    [Fact]
    public async Task Start_automation_session_resolves_workspace_relative_input_paths_under_workspace_directory()
    {
        using var fixture = new AutomationRuntimeFixture();
        await fixture.PrepareWorkspaceContextAsync();
        await using var service = fixture.CreateService();

        var definition = CreateDefinition(
            id: "auto-relative-inputs",
            title: "Relative Inputs",
            prompt: "Read daily note through normalized relative path.",
            inputPaths:
            [
                "tasks/daily-note.md",
            ]);

        var handle = await service.StartAutomationSessionAsync(definition);
        var runResult = await handle.Agent.RunAsync("run");

        runResult.Success.Should().BeTrue();
        fixture.ModelProvider.LastRequest.Should().NotBeNull();
        fixture.ModelProvider.LastRequest!.SystemPrompt.Should().Contain("### File: workspace/tasks/daily-note.md");
    }

    [Fact]
    public async Task Start_automation_session_creates_session_directory_and_returns_handle_identity()
    {
        using var fixture = new AutomationRuntimeFixture();
        await fixture.PrepareWorkspaceContextAsync();
        await using var service = fixture.CreateService();

        var definition = CreateDefinition(
            id: "automation-identity-check",
            title: "Identity Check",
            prompt: "Read context and report identity.");

        var handle = await service.StartAutomationSessionAsync(definition);

        Directory.Exists(handle.SessionDirectory).Should().BeTrue();
        handle.SessionId.Should().Contain("auto-");
        handle.AutomationId.Should().Be("automation-identity-check");
    }

    [Fact]
    public async Task Start_automation_session_records_truncation_evidence_when_prompt_budget_is_exceeded()
    {
        using var fixture = new AutomationRuntimeFixture();
        await fixture.PrepareWorkspaceContextAsync();
        await fixture.WriteLargeInputAsync(
            "tasks/oversized-note.md",
            string.Join(Environment.NewLine, Enumerable.Repeat("oversized-context-line", 200)));
        await using var service = fixture.CreateService(new AutomationSessionOptions
        {
            Model = "automation-capture-model",
            MaxIterations = 4,
            MaxPromptCharacters = 420,
        });

        var definition = CreateDefinition(
            id: "auto-oversized-budget",
            title: "Oversized Budget",
            prompt: "Inspect a very large note and summarize only if budget allows.",
            inputPaths:
            [
                "tasks/oversized-note.md",
            ]);

        var handle = await service.StartAutomationSessionAsync(definition);
        var promptReport = await SessionPromptReportStore.TryReadAsync(handle.SessionDirectory);

        promptReport.Should().NotBeNull();
        promptReport!.WasTruncated.Should().BeTrue();
        promptReport.CharacterBudget.Should().Be(420);
        promptReport.RemainingCharacterBudget.Should().BeGreaterThanOrEqualTo(0);
        promptReport.TruncatedContextFiles.Should().Contain("workspace/tasks/oversized-note.md");
        promptReport.TruncationNotes.Should().Contain(note => note.Contains("oversized-note.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_automation_session_keeps_long_term_memory_out_unless_explicitly_loaded()
    {
        using var fixture = new AutomationRuntimeFixture();
        await fixture.PrepareWorkspaceContextAsync();
        await fixture.WriteLargeInputAsync(
            KodaClawWorkspaceLayout.MemoryFile,
            """
            # Memory

            - Private memory anchor: do not load automatically.
            """);
        await using var service = fixture.CreateService();

        var definition = CreateDefinition(
            id: "auto-memory-boundary",
            title: "Memory Boundary",
            prompt: "Summarize only the explicitly provided operating context.");

        var handle = await service.StartAutomationSessionAsync(definition);
        var runResult = await handle.Agent.RunAsync("run");

        runResult.Success.Should().BeTrue();
        fixture.ModelProvider.LastRequest.Should().NotBeNull();
        fixture.ModelProvider.LastRequest!.SystemPrompt.Should().Contain(
            "Do not infer or recall workspace/MEMORY.md unless it was explicitly loaded as an automation input.");
        fixture.ModelProvider.LastRequest.SystemPrompt.Should().NotContain("### File: workspace/MEMORY.md");

        var promptReport = await SessionPromptReportStore.TryReadAsync(handle.SessionDirectory);
        promptReport.Should().NotBeNull();
        promptReport!.LoadedContextFiles.Should().NotContain("workspace/MEMORY.md");
    }

    private sealed class AutomationRuntimeFixture : IDisposable
    {
        public AutomationRuntimeFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-automation-runtime-tests", Guid.NewGuid().ToString("N"));
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

            var heartbeatPath = Path.Combine(
                RootPath,
                KodaClawWorkspaceLayout.WorkspaceDirectory,
                KodaClawWorkspaceLayout.HeartbeatFile);
            await File.WriteAllTextAsync(
                heartbeatPath,
                """
                # Heartbeat

                - Heartbeat anchor: review QA and send noon digest.
                """);

            var inputPath = Path.Combine(
                RootPath,
                KodaClawWorkspaceLayout.WorkspaceDirectory,
                "tasks",
                "daily-note.md");
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
            await File.WriteAllTextAsync(
                inputPath,
                """
                # Daily Note

                - Daily note anchor: collect incident digests.
                """);
        }

        public async Task WriteLargeInputAsync(string relativePath, string content)
        {
            var inputPath = Path.Combine(
                RootPath,
                KodaClawWorkspaceLayout.WorkspaceDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
            await File.WriteAllTextAsync(inputPath, content);
        }

        public AutomationSessionService CreateService(AutomationSessionOptions? options = null)
        {
            var dependencyFactory = new DefaultMainSessionAgentDependenciesFactory(new MainSessionDependencies
            {
                ModelProvider = ModelProvider,
            });

            return new AutomationSessionService(
                Workspace,
                dependencyFactory,
                options ?? new AutomationSessionOptions
                {
                    Model = "automation-capture-model",
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
                TextDelta = "automation-ok",
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
                        Text = "automation-ok",
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

    private static AutomationDefinitionContract CreateDefinition(
        string id,
        string title,
        string prompt,
        IReadOnlyList<string>? inputPaths = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AutomationDefinitionContract(
            Id: id,
            Title: title,
            Prompt: prompt,
            Source: AutomationDefinitionSource.Manual,
            SourcePath: null,
            CronExpression: "0 * * * *",
            Enabled: true,
            InputPaths: inputPaths,
            ModelId: null,
            NotificationChannels: null,
            NotifyMode: AutomationNotifyMode.None,
            CreatedAt: now,
            UpdatedAt: now,
            LastRunAt: null,
            NextRunAt: null,
            LastRunStatus: null,
            LastError: null);
    }
}
