using FluentAssertions;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Git;
using Xunit;

namespace KodaClaw.UnitTests.Workspace;

/// <summary>
/// L1 单元测试 — WorkspaceGitService
/// 使用真实 LibGit2Sharp（不 mock），全部在 TempDir 中运行。
/// </summary>
public sealed class WorkspaceGitServiceTests : IDisposable
{
    private readonly string _root;

    public WorkspaceGitServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kodaclaw-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            // libgit2sharp 在 .git/objects 下写只读文件，需强制删除
            ForceDeleteDirectory(_root);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private WorkspaceGitService CreateService()
    {
        return new WorkspaceGitService(new KodaClawWorkspaceOptions { RootPath = _root });
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static void ForceDeleteDirectory(string path)
    {
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }

    // ── tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureGitRepoAsync_initialises_empty_workspace_as_new_repo()
    {
        var svc = CreateService();

        await svc.EnsureGitRepoAsync();

        Directory.Exists(Path.Combine(_root, ".git")).Should().BeTrue();
        File.Exists(Path.Combine(_root, ".gitignore")).Should().BeTrue();
        File.Exists(Path.Combine(_root, ".gitattributes")).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureGitRepoAsync_is_idempotent()
    {
        var svc = CreateService();

        await svc.EnsureGitRepoAsync();
        await svc.EnsureGitRepoAsync(); // second call should not throw

        Directory.Exists(Path.Combine(_root, ".git")).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureGitRepoAsync_migrates_existing_workspace_files()
    {
        // Place pre-existing workspace files before initialisation
        WriteFile(_root, "workspace/IDENTITY.md", "# Identity");
        WriteFile(_root, "workspace/SOUL.md", "# Soul");

        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        var commits = await svc.GetRecentCommitsAsync(10);
        commits.Should().NotBeEmpty();
        // The init commit includes the pre-existing files regardless of the message tag
        commits[0].Message.Should().ContainAny("system/init", "system/migrate");
        // Initial commit lists tree entries; LibGit2Sharp may show "workspace" dir or individual files
        commits[0].ChangedFiles.Should().Contain(f => f.Contains("workspace"));
    }

    [Fact]
    public async Task EnsureGitRepoAsync_recovers_from_corrupted_git_directory()
    {
        // Create a broken .git directory (no HEAD, no objects)
        var gitDir = Path.Combine(_root, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "JUNK"), "not a valid git repo");

        var svc = CreateService();

        // Should not throw; should delete corrupted .git and re-initialise
        Func<Task> act = () => svc.EnsureGitRepoAsync();
        await act.Should().NotThrowAsync();

        Directory.Exists(gitDir).Should().BeTrue();
        File.Exists(Path.Combine(_root, ".gitignore")).Should().BeTrue();
    }

    [Fact]
    public async Task TryCommitAsync_returns_false_when_nothing_staged()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        var committed = await svc.TryCommitAsync("workspace(test)[agent]: no changes");

        committed.Should().BeFalse();
    }

    [Fact]
    public async Task TryCommitAsync_returns_true_and_creates_commit_for_tracked_file()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        WriteFile(_root, "workspace/IDENTITY.md", "# New identity");

        var committed = await svc.TryCommitAsync("workspace(identity)[agent]: update");

        committed.Should().BeTrue();

        var commits = await svc.GetRecentCommitsAsync(5);
        commits[0].Message.Should().Contain("workspace(identity)");
        commits[0].ChangedFiles.Should().Contain(f => f.Contains("IDENTITY.md"));
    }

    [Fact]
    public async Task TryCommitAsync_ignores_files_outside_tracked_paths()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        // sessions/ is in .gitignore, not in TrackedPaths — should never be staged
        WriteFile(_root, "sessions/session-001.json", "{}");

        var committed = await svc.TryCommitAsync("workspace(sessions)[agent]: should not commit");

        committed.Should().BeFalse();
    }

    [Fact]
    public async Task GetRecentCommitsAsync_respects_limit()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        for (var i = 1; i <= 5; i++)
        {
            WriteFile(_root, "workspace/IDENTITY.md", $"# Version {i}");
            await svc.TryCommitAsync($"workspace(identity)[agent]: v{i}");
        }

        var commits = await svc.GetRecentCommitsAsync(3);

        commits.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetCommitDiffAsync_returns_non_empty_diff_for_file_change()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        WriteFile(_root, "workspace/IDENTITY.md", "# Original");
        await svc.TryCommitAsync("workspace(identity)[agent]: initial");

        WriteFile(_root, "workspace/IDENTITY.md", "# Updated");
        await svc.TryCommitAsync("workspace(identity)[agent]: update");

        var commits = await svc.GetRecentCommitsAsync(2);
        var diff = await svc.GetCommitDiffAsync(commits[0].Hash);

        diff.Should().Contain("IDENTITY.md");
        diff.Should().Contain("+# Updated");
    }

    [Fact]
    public async Task RevertFileToCommitAsync_reverts_file_content_and_creates_new_commit()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        WriteFile(_root, "workspace/IDENTITY.md", "# Original content");
        await svc.TryCommitAsync("workspace(identity)[agent]: initial");

        var commits = await svc.GetRecentCommitsAsync(5);
        var originalHash = commits[0].Hash;

        WriteFile(_root, "workspace/IDENTITY.md", "# Modified content");
        await svc.TryCommitAsync("workspace(identity)[agent]: modify");

        // Revert file to the original commit
        var newHash = await svc.RevertFileToCommitAsync(originalHash, "workspace/IDENTITY.md");

        var fileContent = File.ReadAllText(Path.Combine(_root, "workspace/IDENTITY.md"));
        fileContent.Should().Contain("Original content");
        newHash.Should().NotBeNullOrEmpty();
        newHash.Should().NotBe(originalHash);
    }

    [Fact]
    public async Task RevertFileToCommitAsync_rejects_config_path_outside_workspace()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        WriteFile(_root, "config/models.json", "{}");
        await svc.TryCommitAsync("config[system/init]: init");

        var commits = await svc.GetRecentCommitsAsync(5);

        Func<Task> act = () => svc.RevertFileToCommitAsync(commits[0].Hash, "config/models.json");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*workspace/*");
    }

    [Fact]
    public async Task RevertFileToCommitAsync_rejects_path_traversal_attempt()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        WriteFile(_root, "workspace/IDENTITY.md", "# Identity");
        await svc.TryCommitAsync("workspace(identity)[agent]: init");
        var commits = await svc.GetRecentCommitsAsync(1);

        Func<Task> act = () => svc.RevertFileToCommitAsync(
            commits[0].Hash, "workspace/../config/gateway.json");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Concurrent_commits_do_not_throw()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        // Fire 5 concurrent commit tasks — the SemaphoreSlim must prevent lock file conflicts
        var tasks = Enumerable.Range(1, 5).Select(async i =>
        {
            WriteFile(_root, $"workspace/IDENTITY.md", $"# Content {i}");
            return await svc.TryCommitAsync($"workspace(identity)[agent]: concurrent {i}");
        });

        var results = await Task.WhenAll(tasks);

        // At least one commit should succeed
        results.Should().Contain(true);
    }

    // ── .gitignore version & upgrade tests ──────────────────────────────

    [Fact]
    public void ParseGitIgnoreVersion_returns_version_from_marker()
    {
        var content = "# kodaclaw-gitignore-version:2\n\nsessions/\n";
        WorkspaceGitService.ParseGitIgnoreVersion(content).Should().Be(2);
    }

    [Fact]
    public void ParseGitIgnoreVersion_returns_1_when_no_marker()
    {
        var content = "# Runtime directories\nsessions/\nlogs/\n";
        WorkspaceGitService.ParseGitIgnoreVersion(content).Should().Be(1);
    }

    [Fact]
    public void ParseGitIgnoreVersion_handles_whitespace_around_number()
    {
        var content = "# kodaclaw-gitignore-version:  3  \nsessions/\n";
        WorkspaceGitService.ParseGitIgnoreVersion(content).Should().Be(3);
    }

    [Fact]
    public async Task EnsureGitRepoAsync_upgrades_stale_gitignore_on_existing_repo()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        // Simulate a v1 .gitignore (no version marker, no memory rules)
        var gitignorePath = Path.Combine(_root, ".gitignore");
        File.WriteAllText(gitignorePath, "# Runtime directories\nsessions/\nlogs/\n");

        // Second call should detect stale version and upgrade
        await svc.EnsureGitRepoAsync();

        var upgraded = File.ReadAllText(gitignorePath);
        upgraded.Should().Contain("workspace/memory/sessions/",
            because: "v2 .gitignore must ignore transient session summaries");
        upgraded.Should().Contain("kodaclaw-gitignore-version:",
            because: "upgraded .gitignore must have version marker");
    }

    [Fact]
    public async Task EnsureGitRepoAsync_does_not_rewrite_current_version_gitignore()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        // Record the current .gitignore write time
        var gitignorePath = Path.Combine(_root, ".gitignore");
        var writeTimeBefore = File.GetLastWriteTimeUtc(gitignorePath);

        // Small delay so timestamp would differ if rewritten
        await Task.Delay(50);
        await svc.EnsureGitRepoAsync();

        File.GetLastWriteTimeUtc(gitignorePath).Should().Be(writeTimeBefore,
            because: "current-version .gitignore should not be rewritten");
    }

    [Fact]
    public async Task Memory_sessions_directory_is_ignored_by_git()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        // Write a file under workspace/memory/sessions/ — should be ignored
        WriteFile(_root, "workspace/memory/sessions/session-001.md", "# Summary");

        var committed = await svc.TryCommitAsync("workspace(memory)[agent]: should not include sessions");

        committed.Should().BeFalse(
            because: "workspace/memory/sessions/ is in .gitignore");
    }

    [Fact]
    public async Task Memory_daily_log_is_ignored_by_git()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        // Write a daily log file — should be ignored
        WriteFile(_root, "workspace/memory/2026-03-26.md", "# Daily log");

        var committed = await svc.TryCommitAsync("workspace(memory)[agent]: should not include daily log");

        committed.Should().BeFalse(
            because: "workspace/memory/YYYY-MM-DD.md is in .gitignore");
    }

    [Fact]
    public async Task Memory_topics_directory_is_tracked_by_git()
    {
        var svc = CreateService();
        await svc.EnsureGitRepoAsync();

        // Write a topic file — should be tracked (not ignored)
        WriteFile(_root, "workspace/memory/topics/coding-patterns.md", "# Coding Patterns");

        var committed = await svc.TryCommitAsync("workspace(memory)[agent]: add topic");

        committed.Should().BeTrue(
            because: "workspace/memory/topics/ must be version-tracked");
    }
}
