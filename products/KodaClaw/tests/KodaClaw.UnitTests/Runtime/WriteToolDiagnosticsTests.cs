using System.IO;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Tools;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

/// <summary>
/// Unit tests verifying that write-operation tools emit diagnostic events (KC-6203 / KC-6206).
/// </summary>
public sealed class WriteToolDiagnosticsTests : IDisposable
{
    private readonly string _rootPath;

    public WriteToolDiagnosticsTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "kodaclaw-diag-tool-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    // -----------------------------------------------------------------------
    // WorkspaceMemoryAppendTool
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WorkspaceMemoryAppendTool_emits_workspace_memory_appended_event()
    {
        var diagnostics = new CollectingDiagnosticsService();
        var workspaceService = new StubWorkspaceService(_rootPath);
        var tool = new WorkspaceMemoryAppendTool(workspaceService, diagnostics);

        var args = new WorkspaceMemoryAppendArgs
        {
            Content = "Remembered something important.",
            Priority = "lasting",
        };

        await tool.ExecuteAsync((object)args, MakeContext(), CancellationToken.None);

        diagnostics.Events.Should().ContainSingle(e =>
            e.EventType == "workspace.memory_appended" && e.Level == "info");
    }

    // -----------------------------------------------------------------------
    // WorkspaceProtocolUpdateTool
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WorkspaceProtocolUpdateTool_emits_workspace_protocol_updated_event()
    {
        var diagnostics = new CollectingDiagnosticsService();
        var workspaceService = new StubWorkspaceService(_rootPath);
        var tool = new WorkspaceProtocolUpdateTool(workspaceService, diagnostics);

        var args = new WorkspaceProtocolUpdateArgs
        {
            Target = "identity",
            Section = null,
            Content = "I am Koda.",
        };

        await tool.ExecuteAsync((object)args, MakeContext(), CancellationToken.None);

        diagnostics.Events.Should().ContainSingle(e =>
            e.EventType == "workspace.protocol_updated" && e.Level == "info");
    }

    // -----------------------------------------------------------------------
    // InboxCreateTool
    // -----------------------------------------------------------------------

    [Fact]
    public async Task InboxCreateTool_emits_inbox_item_created_event()
    {
        var diagnostics = new CollectingDiagnosticsService();
        var inboxRepo = new InMemoryInboxRepository();
        var tool = new InboxCreateTool(inboxRepo, diagnosticsService: diagnostics);

        var args = new InboxCreateArgs
        {
            Title = "Check this out",
            Summary = "Agent found something worth noting.",
        };

        await tool.ExecuteAsync((object)args, MakeContext(), CancellationToken.None);

        diagnostics.Events.Should().ContainSingle(e =>
            e.EventType == "inbox.item_created" && e.Level == "info");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static ToolContext MakeContext()
    {
        var sandbox = new Mock<ISandbox>().Object;
        return new ToolContext
        {
            CallId = "test-call",
            AgentId = "test-agent",
            Sandbox = sandbox,
        };
    }

    // -----------------------------------------------------------------------
    // Stubs
    // -----------------------------------------------------------------------

    private sealed class CollectingDiagnosticsService : IDiagnosticsService
    {
        private readonly List<DiagnosticEvent> _events = [];
        public IReadOnlyList<DiagnosticEvent> Events => _events;

        public void Record(DiagnosticEvent diagnosticEvent) => _events.Add(diagnosticEvent);

        public IReadOnlyList<DiagnosticEvent> GetRecent(int limit = 50, string? correlationId = null) => _events;

        public IReadOnlyList<DiagnosticEvent> Query(DiagnosticsQuery? query = null) => _events;

        public DiagnosticsStatsResponse GetStats(DateTimeOffset? since = null) => new(0, 0, 0, [], null, null);

        public Task ClearAsync(DateTimeOffset? before = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<DiagnosticEvent> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StubWorkspaceService : IWorkspaceService
    {
        public StubWorkspaceService(string rootPath) => RootPath = rootPath;

        public string RootPath { get; }

        public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceSnapshot(RootPath, KodaClawWorkspaceLayout.CurrentWorkspaceVersion, true, false, null, null));

        public Task<WorkspaceSnapshot> EnsureInitializedAsync(CancellationToken cancellationToken = default)
            => GetSnapshotAsync(cancellationToken);

        public Task<WorkspaceAppConfig> LoadAppConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceAppConfig());

        public Task SaveAppConfigAsync(WorkspaceAppConfig appConfig, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public string GetSessionDirectory(string sessionId)
            => Path.Combine(RootPath, KodaClawWorkspaceLayout.SessionsDirectory, sessionId);

        public IReadOnlyList<string> GetSkillsPaths() => [];

        public Task<WorkspaceMcpConfig> ReadMcpConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceMcpConfig());

        public Task SaveMcpConfigAsync(WorkspaceMcpConfig config, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<GatewayConfig> ReadGatewayConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GatewayConfig());

        public Task SaveGatewayConfigAsync(GatewayConfig config, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCommitWorkspaceAsync(string message, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class InMemoryInboxRepository : IInboxRepository
    {
        private readonly Dictionary<string, InboxItem> _items = new(StringComparer.Ordinal);

        public Task UpsertAsync(InboxItem item, CancellationToken _ = default)
        {
            _items[item.Id] = item;
            return Task.CompletedTask;
        }

        public Task<InboxItem?> GetByIdAsync(string id, CancellationToken _ = default)
        {
            _items.TryGetValue(id, out var r);
            return Task.FromResult(r);
        }

        public Task<IReadOnlyList<InboxItem>> ListAsync(InboxQuery? query = null, CancellationToken _ = default)
        {
            IReadOnlyList<InboxItem> result = _items.Values.ToList();
            return Task.FromResult(result);
        }

        public Task<bool> UpdateStatusAsync(
            string id, InboxItemStatus status, DateTimeOffset updatedAt,
            DateTimeOffset? resolvedAt = null, CancellationToken _ = default)
        {
            if (!_items.TryGetValue(id, out var item)) return Task.FromResult(false);
            _items[id] = item with { Status = status, UpdatedAt = updatedAt, ResolvedAt = resolvedAt };
            return Task.FromResult(true);
        }
    }
}
