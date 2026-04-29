namespace KodaClaw.Contracts.Workspace;

public sealed record WorkspaceGitCommit(
    string Hash,
    string ShortHash,
    string Message,
    string Author,
    DateTimeOffset CommittedAt,
    IReadOnlyList<string> ChangedFiles);

public sealed record WorkspaceGitLogResponse(
    IReadOnlyList<WorkspaceGitCommit> Commits,
    bool HasMore);

public sealed record WorkspaceGitRevertFileRequest(
    string Hash,
    string FilePath);

public sealed record WorkspaceGitRevertFileResponse(
    string NewHash);
