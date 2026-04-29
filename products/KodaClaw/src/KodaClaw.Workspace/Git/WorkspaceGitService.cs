using KodaClaw.Contracts.Workspace;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Workspace.Git;

/// <summary>
/// Implements git-based version tracking for workspace protocol files.
/// Registered as Singleton — the SemaphoreSlim must be shared across all callers.
/// </summary>
public sealed class WorkspaceGitService : IWorkspaceGitService
{
    private static readonly string[] TrackedPaths =
    [
        "workspace/",
        "config/models.json",
        "config/plugins.json",
        "config/app.json",
    ];

    /// <summary>
    /// Bump this constant whenever GitIgnoreContent changes.
    /// EnsureGitRepoAsync uses it to detect stale .gitignore files on existing repos.
    /// </summary>
    private const int GitIgnoreVersion = 3;
    private const string GitIgnoreVersionMarker = "# kodaclaw-gitignore-version:";

    private static readonly string GitIgnoreContent =
        $"""
        {GitIgnoreVersionMarker}{GitIgnoreVersion}

        # Runtime directories
        sessions/
        logs/
        cache/
        media/
        state/

        # Internal system state (approvals, inbox, audit logs, channel bindings, etc.)
        .koda/

        # Sensitive identity files
        identity/device.json
        identity/profile.json

        # Runtime / sensitive config files
        config/gateway.json
        config/onboarding.json
        config/startup-repair-report.json
        config/update-state.json
        config/import-repair-report.json

        # Large workspace artifacts (keep .md knowledge files)
        workspace/canvas/artifacts/
        workspace/knowledge/*
        !workspace/knowledge/**/*.md

        # Memory: transient session summaries and daily logs (topics/dormant/archive ARE tracked)
        workspace/memory/sessions/
        workspace/memory/????-??-??.md
        """;

    private static readonly string GitAttributesContent =
        """
        * text=auto eol=lf
        *.md text eol=lf
        *.json text eol=lf
        """;

    private readonly string _rootPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<WorkspaceGitService> _logger;

    public WorkspaceGitService(KodaClawWorkspaceOptions options, ILogger<WorkspaceGitService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _rootPath = options.ResolveRootPath();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceGitService>.Instance;
    }

    public async Task EnsureGitRepoAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var gitDir = Path.Combine(_rootPath, ".git");

            // Corrupted .git: directory exists but is not a valid repo.
            if (Directory.Exists(gitDir) && !Repository.IsValid(_rootPath))
            {
                Directory.Delete(gitDir, recursive: true);
            }

            if (Repository.IsValid(_rootPath))
            {
                await UpgradeGitIgnoreIfNeededAsync(cancellationToken);
                return;
            }

            // Not a git repo yet — initialise.
            Repository.Init(_rootPath);

            await WriteAncillaryFilesAsync(cancellationToken);

            using var repo = new Repository(_rootPath);
            var sig = BuildSignature();

            // Stage ancillary files unconditionally.
            Commands.Stage(repo, ".gitignore");
            Commands.Stage(repo, ".gitattributes");

            // Determine if this is a fresh workspace or an existing one being migrated.
            StageTrackedPaths(repo);

            var hasContent = repo.RetrieveStatus().Staged.Any();
            var isNewWorkspace = !hasContent ||
                repo.RetrieveStatus().Staged.All(e =>
                    e.FilePath is ".gitignore" or ".gitattributes");

            var message = isNewWorkspace
                ? "workspace(init)[system/init]: initialize workspace repository"
                : "workspace(migrate)[system/migrate]: import existing workspace into git";

            repo.Commit(message, sig, sig);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> TryCommitAsync(string message, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!Repository.IsValid(_rootPath))
                return false;

            using var repo = new Repository(_rootPath);
            StageTrackedPaths(repo);

            if (!repo.RetrieveStatus().IsDirty)
                return false;

            var sig = BuildSignature();
            repo.Commit(message, sig, sig);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Git commit failed for workspace at {RootPath}", _rootPath);
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<IReadOnlyList<WorkspaceGitCommit>> GetRecentCommitsAsync(
        int limit = 50, int skip = 0, CancellationToken cancellationToken = default)
    {
        if (!Repository.IsValid(_rootPath))
            return Task.FromResult<IReadOnlyList<WorkspaceGitCommit>>([]);

        try
        {
            using var repo = new Repository(_rootPath);
            var commits = repo.Commits
                .Skip(skip)
                .Take(limit)
                .Select(c => new WorkspaceGitCommit(
                    Hash: c.Sha,
                    ShortHash: c.Sha[..7],
                    Message: c.MessageShort,
                    Author: c.Author.Name,
                    CommittedAt: c.Author.When,
                    ChangedFiles: GetChangedFiles(repo, c)))
                .ToList();

            return Task.FromResult<IReadOnlyList<WorkspaceGitCommit>>(commits);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read git commits from {RootPath}", _rootPath);
            return Task.FromResult<IReadOnlyList<WorkspaceGitCommit>>([]);
        }
    }

    public Task<string> GetCommitDiffAsync(string hash, CancellationToken cancellationToken = default)
    {
        if (!Repository.IsValid(_rootPath))
            return Task.FromResult(string.Empty);

        try
        {
            using var repo = new Repository(_rootPath);
            var commit = repo.Lookup<Commit>(hash);
            if (commit is null)
                return Task.FromResult(string.Empty);

            var parent = commit.Parents.FirstOrDefault();
            var patch = repo.Diff.Compare<Patch>(
                parent?.Tree,
                commit.Tree);

            return Task.FromResult(patch.Content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read git diff for commit {Hash}", hash);
            return Task.FromResult(string.Empty);
        }
    }

    public async Task<string> RevertFileToCommitAsync(
        string hash,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        // Security: only allow workspace/ prefix, no path traversal.
        if (!filePath.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only files under workspace/ may be reverted.", nameof(filePath));

        if (filePath.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Path traversal is not permitted.", nameof(filePath));

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var repo = new Repository(_rootPath);
            var commit = repo.Lookup<Commit>(hash)
                ?? throw new InvalidOperationException($"Commit '{hash}' not found.");

            // Read the blob at the target commit.
            var treeEntry = commit[filePath]
                ?? throw new InvalidOperationException($"File '{filePath}' not found in commit '{hash}'.");

            var blob = (Blob)treeEntry.Target;
            var absolutePath = Path.Combine(_rootPath, filePath.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await using (var stream = blob.GetContentStream())
            await using (var writer = new FileStream(absolutePath, FileMode.Create, FileAccess.Write))
            {
                await stream.CopyToAsync(writer, cancellationToken);
            }

            // Stage and commit the reverted file.
            Commands.Stage(repo, filePath);
            var sig = BuildSignature();
            var shortHash = hash.Length >= 7 ? hash[..7] : hash;
            var message = $"workspace({Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant()})" +
                          $"[user/settings-desk]: revert to {shortHash}";
            var newCommit = repo.Commit(message, sig, sig);
            return newCommit.Sha;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void StageTrackedPaths(Repository repo)
    {
        foreach (var trackedPath in TrackedPaths)
        {
            try
            {
                Commands.Stage(repo, trackedPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping unresolvable tracked path {Path} during staging", trackedPath);
            }
        }
    }

    private static IReadOnlyList<string> GetChangedFiles(Repository repo, Commit commit)
    {
        var parent = commit.Parents.FirstOrDefault();
        if (parent is null)
        {
            // Initial commit: list all files in the tree.
            return commit.Tree
                .Select(e => e.Path)
                .ToList();
        }

        return repo.Diff.Compare<TreeChanges>(parent.Tree, commit.Tree)
            .Select(c => c.Path)
            .ToList();
    }

    private async Task WriteAncillaryFilesAsync(CancellationToken cancellationToken)
    {
        var gitignorePath = Path.Combine(_rootPath, ".gitignore");
        if (!File.Exists(gitignorePath))
            await File.WriteAllTextAsync(gitignorePath, GitIgnoreContent, cancellationToken);

        var gitattributesPath = Path.Combine(_rootPath, ".gitattributes");
        if (!File.Exists(gitattributesPath))
            await File.WriteAllTextAsync(gitattributesPath, GitAttributesContent, cancellationToken);
    }

    /// <summary>
    /// Detects stale .gitignore via version marker and overwrites if needed.
    /// Commits the upgrade so the change is tracked in workspace history.
    /// </summary>
    private async Task UpgradeGitIgnoreIfNeededAsync(CancellationToken cancellationToken)
    {
        var gitignorePath = Path.Combine(_rootPath, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            // No .gitignore at all — write it fresh and commit.
            await File.WriteAllTextAsync(gitignorePath, GitIgnoreContent, cancellationToken);
            CommitGitIgnoreUpgrade();
            return;
        }

        var existingVersion = ParseGitIgnoreVersion(
            await File.ReadAllTextAsync(gitignorePath, cancellationToken));

        if (existingVersion >= GitIgnoreVersion)
            return;

        await File.WriteAllTextAsync(gitignorePath, GitIgnoreContent, cancellationToken);
        CommitGitIgnoreUpgrade();
    }

    private void CommitGitIgnoreUpgrade()
    {
        try
        {
            using var repo = new Repository(_rootPath);
            Commands.Stage(repo, ".gitignore");
            if (!repo.RetrieveStatus().IsDirty)
                return;
            var sig = BuildSignature();
            repo.Commit(
                $"workspace(gitignore)[system/upgrade]: upgrade .gitignore to v{GitIgnoreVersion}",
                sig, sig);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Best-effort .gitignore upgrade commit failed for {RootPath}", _rootPath);
        }
    }

    internal static int ParseGitIgnoreVersion(string content)
    {
        foreach (var line in content.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(GitIgnoreVersionMarker.AsSpan(), StringComparison.Ordinal))
            {
                var versionPart = trimmed[GitIgnoreVersionMarker.Length..].Trim();
                if (int.TryParse(versionPart, out var v))
                    return v;
            }
        }
        // No marker found — treat as version 1 (original format).
        return 1;
    }

    private static Signature BuildSignature() =>
        new("KodaClaw", "agent@kodaclaw.local", DateTimeOffset.UtcNow);
}
