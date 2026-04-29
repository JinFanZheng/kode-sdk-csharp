using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Memory;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Workspace;

public sealed class MemoryFileServiceTests : IDisposable
{
    private readonly string _rootPath;
    private readonly Mock<IWorkspaceService> _workspaceMock;
    private readonly MemoryFileService _service;

    public MemoryFileServiceTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"kodaclaw-memfs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory));

        _workspaceMock = new Mock<IWorkspaceService>();
        _workspaceMock.SetupGet(w => w.RootPath).Returns(_rootPath);

        _service = new MemoryFileService(_workspaceMock.Object);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootPath, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task GetStats_CountsAllDirectories()
    {
        WriteMemoryMd("# Long-Term Memory\n\n## Entry A\nContent A\n\n## Entry B\nContent B\n");
        CreateFiles(KodaClawWorkspaceLayout.MemoryDormantDirectory, 2);
        CreateFiles(KodaClawWorkspaceLayout.MemoryArchiveDirectory, 3);
        CreateFiles(KodaClawWorkspaceLayout.MemoryTopicsDirectory, 1);
        CreateFiles(KodaClawWorkspaceLayout.MemorySessionsDirectory, 4);

        var stats = await _service.GetStatsAsync();

        stats.ActiveCount.Should().Be(2);
        stats.DormantCount.Should().Be(2);
        stats.ArchivedCount.Should().Be(3);
        stats.TopicsCount.Should().Be(1);
        stats.SessionsCount.Should().Be(4);
    }

    [Fact]
    public async Task GetStats_EmptyWorkspace_AllZero()
    {
        var stats = await _service.GetStatsAsync();

        stats.ActiveCount.Should().Be(0);
        stats.DormantCount.Should().Be(0);
        stats.ArchivedCount.Should().Be(0);
        stats.TopicsCount.Should().Be(0);
        stats.SessionsCount.Should().Be(0);
    }

    [Fact]
    public async Task ListEntries_ReturnsActiveFromMemoryMd()
    {
        WriteMemoryMd("# Long-Term Memory\n\n## User Preferences\nLikes dark mode\n\n## Project Info\nKodaClaw dev\n");

        var entries = await _service.ListEntriesAsync(statusFilter: "active");

        entries.Should().HaveCount(2);
        entries.Should().Contain(e => e.Key == "user-preferences" && e.Status == "active");
        entries.Should().Contain(e => e.Key == "project-info" && e.Status == "active");
    }

    [Fact]
    public async Task ListEntries_ReturnsDormantFromDirectory()
    {
        var dormantDir = Path.Combine(_rootPath, KodaClawWorkspaceLayout.MemoryDormantDirectory);
        Directory.CreateDirectory(dormantDir);
        File.WriteAllText(Path.Combine(dormantDir, "old-topic.md"),
            "---\ntitle: Old Topic\npriority: standard\nstatus: dormant\n---\nStale content.\n");

        var entries = await _service.ListEntriesAsync(statusFilter: "dormant");

        entries.Should().HaveCount(1);
        entries[0].Key.Should().Be("old-topic");
        entries[0].Status.Should().Be("dormant");
        entries[0].Title.Should().Be("Old Topic");
    }

    [Fact]
    public async Task ListEntries_StatusFilter_ExcludesOtherStatuses()
    {
        WriteMemoryMd("# Long-Term Memory\n\n## Active One\nContent\n");
        CreateFiles(KodaClawWorkspaceLayout.MemoryDormantDirectory, 1);

        var activeOnly = await _service.ListEntriesAsync(statusFilter: "active");
        activeOnly.Should().AllSatisfy(e => e.Status.Should().Be("active"));

        var dormantOnly = await _service.ListEntriesAsync(statusFilter: "dormant");
        dormantOnly.Should().AllSatisfy(e => e.Status.Should().Be("dormant"));
    }

    [Fact]
    public async Task PromoteEntry_MovesFromDormantToMemoryMd()
    {
        var dormantDir = Path.Combine(_rootPath, KodaClawWorkspaceLayout.MemoryDormantDirectory);
        Directory.CreateDirectory(dormantDir);
        File.WriteAllText(Path.Combine(dormantDir, "revived-topic.md"),
            "---\ntitle: Revived Topic\npriority: standard\nstatus: dormant\n---\n# Revived Topic\nImportant stuff.\n");

        WriteMemoryMd("# Long-Term Memory\n\n## Existing\nAlready here.\n");

        var result = await _service.PromoteEntryAsync("revived-topic");

        result.Should().BeTrue();
        File.Exists(Path.Combine(dormantDir, "revived-topic.md")).Should().BeFalse();

        var memoryContent = File.ReadAllText(Path.Combine(
            _rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.MemoryFile));
        memoryContent.Should().Contain("## Revived Topic");
        memoryContent.Should().Contain("Important stuff.");
    }

    [Fact]
    public async Task PromoteEntry_NonExistentKey_ReturnsFalse()
    {
        var result = await _service.PromoteEntryAsync("does-not-exist");
        result.Should().BeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void WriteMemoryMd(string content)
    {
        var path = Path.Combine(_rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.MemoryFile);
        File.WriteAllText(path, content);
    }

    private void CreateFiles(string relativeDir, int count)
    {
        var dir = Path.Combine(_rootPath, relativeDir);
        Directory.CreateDirectory(dir);
        for (var i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(dir, $"entry-{i}.md"),
                $"---\ntitle: Entry {i}\npriority: standard\nstatus: active\n---\nContent {i}\n");
        }
    }
}
