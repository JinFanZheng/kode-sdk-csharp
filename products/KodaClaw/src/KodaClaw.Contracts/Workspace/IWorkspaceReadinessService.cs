namespace KodaClaw.Contracts.Workspace;

public interface IWorkspaceReadinessService
{
    Task<WorkspaceReadinessResponse> GetReadinessAsync(CancellationToken cancellationToken = default);
}
