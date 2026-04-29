namespace KodaClaw.Contracts.Workspace;

public sealed record WorkspaceReadinessResponse(
    bool IsIdentitySet,
    bool IsSoulSet,
    bool IsUserSet,
    bool HasAnyGap);
