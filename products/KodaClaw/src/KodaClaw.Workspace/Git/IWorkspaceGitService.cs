using KodaClaw.Contracts.Workspace;

namespace KodaClaw.Workspace.Git;

/// <summary>
/// Manages the git repository for the workspace.
/// Must be registered as a Singleton — the internal SemaphoreSlim is instance-level.
/// </summary>
public interface IWorkspaceGitService
{
    /// <summary>
    /// Idempotent git repo initialisation. Safe to call on every Gateway startup.
    /// - Already a valid repo → no-op.
    /// - Corrupted .git dir → deletes and re-inits.
    /// - Existing workspace files, no .git → migration commit.
    /// - Brand-new workspace → init commit.
    /// </summary>
    Task EnsureGitRepoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages all tracked workspace files and commits.
    /// Returns false when there is nothing to commit or if git is unavailable.
    /// Never throws — failures are silently swallowed so the calling tool is not affected.
    /// </summary>
    Task<bool> TryCommitAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>Returns a page of commits. Empty list when git is not initialised.</summary>
    Task<IReadOnlyList<WorkspaceGitCommit>> GetRecentCommitsAsync(
        int limit = 50, int skip = 0, CancellationToken cancellationToken = default);

    /// <summary>Returns the unified diff text for the given commit vs its parent.</summary>
    Task<string> GetCommitDiffAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores <paramref name="filePath"/> to the blob recorded in <paramref name="hash"/>
    /// and creates a new revert commit. Returns the new commit hash.
    /// Only paths under workspace/ are permitted; others throw ArgumentException.
    /// </summary>
    Task<string> RevertFileToCommitAsync(
        string hash,
        string filePath,
        CancellationToken cancellationToken = default);
}
