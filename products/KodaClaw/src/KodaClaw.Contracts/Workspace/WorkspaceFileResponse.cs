namespace KodaClaw.Contracts.Workspace;

public sealed record WorkspaceFileResponse(
    string Target,
    string Content);

public sealed record WorkspaceFileUpdateRequest(
    string Content);
