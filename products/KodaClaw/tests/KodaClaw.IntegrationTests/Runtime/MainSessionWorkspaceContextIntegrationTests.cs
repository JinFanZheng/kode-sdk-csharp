using System.IO;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Xunit;

namespace KodaClaw.IntegrationTests.Runtime;

public sealed class MainSessionWorkspaceContextIntegrationTests : IDisposable
{
    private readonly string _rootPath;
    private readonly WorkspaceService _workspace;

    public MainSessionWorkspaceContextIntegrationTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-ws-ctx-tests", Guid.NewGuid().ToString("N"));
        _workspace = new WorkspaceService(new KodaClawWorkspaceOptions { RootPath = _rootPath });
    }

    [Fact]
    public async Task Main_session_prompt_contains_identity_and_soul_from_workspace()
    {
        await _workspace.EnsureInitializedAsync();

        var customIdentity = "# Koda Identity\n\n- Name: TestKoda\n- Role: test assistant";
        var identityPath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.IdentityFile);
        await File.WriteAllTextAsync(identityPath, customIdentity);

        var customSoul = "# Koda Soul\n\n- Rule: always be precise in tests";
        var soulPath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.SoulFile);
        await File.WriteAllTextAsync(soulPath, customSoul);

        await using var service = CreateService();
        var handle = await service.EnsureMainSessionAsync();

        var report = await SessionPromptReportStore.TryReadAsync(handle.SessionDirectory);
        report.Should().NotBeNull();
        report!.SystemPrompt.Should().Contain("TestKoda");
        report.SystemPrompt.Should().Contain("always be precise in tests");
    }

    [Fact]
    public async Task Main_session_prompt_loads_all_five_baseline_workspace_files()
    {
        await _workspace.EnsureInitializedAsync();

        await using var service = CreateService();
        var handle = await service.EnsureMainSessionAsync();

        var report = await SessionPromptReportStore.TryReadAsync(handle.SessionDirectory);
        report.Should().NotBeNull();

        var loaded = report!.LoadedContextFiles;
        loaded.Should().Contain(f => f.Contains(KodaClawWorkspaceLayout.AgentsFile));
        loaded.Should().Contain(f => f.Contains(KodaClawWorkspaceLayout.IdentityFile));
        loaded.Should().Contain(f => f.Contains(KodaClawWorkspaceLayout.SoulFile));
        loaded.Should().Contain(f => f.Contains(KodaClawWorkspaceLayout.UserFile));
        loaded.Should().Contain(f => f.Contains(KodaClawWorkspaceLayout.MemoryFile));
    }

    [Fact]
    public async Task Main_session_prompt_includes_daily_memory_file_when_it_exists()
    {
        await _workspace.EnsureInitializedAsync();

        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var dailyMemoryDir = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, "memory");
        Directory.CreateDirectory(dailyMemoryDir);
        var dailyMemoryPath = Path.Combine(dailyMemoryDir, $"{today}.md");
        await File.WriteAllTextAsync(dailyMemoryPath, $"# Memory {today}\n\n- Remembered: unit test ran today");

        await using var service = CreateService();
        var handle = await service.EnsureMainSessionAsync();

        var report = await SessionPromptReportStore.TryReadAsync(handle.SessionDirectory);
        report.Should().NotBeNull();
        report!.SystemPrompt.Should().Contain("Remembered: unit test ran today");
        report.LoadedContextFiles.Should().Contain(f => f.Contains(today));
    }

    [Fact]
    public async Task Main_session_prompt_skips_daily_memory_file_when_it_does_not_exist()
    {
        await _workspace.EnsureInitializedAsync();

        await using var service = CreateService();
        var handle = await service.EnsureMainSessionAsync();

        var report = await SessionPromptReportStore.TryReadAsync(handle.SessionDirectory);
        report.Should().NotBeNull();

        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        report!.LoadedContextFiles.Should().NotContain(f => f.Contains(today));
    }

    [Fact]
    public async Task Main_session_prompt_contains_memory_md_content()
    {
        await _workspace.EnsureInitializedAsync();

        var memoryContent = "# Long-Term Memory\n\n- Fact: integration tests are important";
        var memoryPath = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.MemoryFile);
        await File.WriteAllTextAsync(memoryPath, memoryContent);

        await using var service = CreateService();
        var handle = await service.EnsureMainSessionAsync();

        var report = await SessionPromptReportStore.TryReadAsync(handle.SessionDirectory);
        report!.SystemPrompt.Should().Contain("integration tests are important");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private MainSessionService CreateService()
    {
        var factory = new DefaultMainSessionAgentDependenciesFactory(new MainSessionDependencies
        {
            ModelProvider = new NoOpModelProvider(),
        });

        return new MainSessionService(
            _workspace,
            factory,
            new MainSessionOptions
            {
                Model = "no-op-model",
                MaxIterations = 1,
            });
    }

    private sealed class NoOpModelProvider : IModelProvider
    {
        public string ProviderName => "no-op";

#pragma warning disable CS1998
        public async IAsyncEnumerable<StreamChunk> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new StreamChunk { Type = StreamChunkType.TextDelta, TextDelta = string.Empty };
            yield return new StreamChunk { Type = StreamChunkType.MessageStop, StopReason = ModelStopReason.EndTurn, Usage = new TokenUsage { InputTokens = 0, OutputTokens = 0 } };
        }
#pragma warning restore CS1998

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelResponse
            {
                Content = [new TextContent { Text = string.Empty }],
                StopReason = ModelStopReason.EndTurn,
                Usage = new TokenUsage { InputTokens = 0, OutputTokens = 0 },
                Model = request.Model,
            });
        }

        public Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public ModelCapabilities? GetModelCapabilities(string modelId) => null;
    }
}
