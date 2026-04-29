using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;

namespace KodaClaw.Workspace;

public sealed class WorkspaceReadinessService : IWorkspaceReadinessService
{
    private readonly IWorkspaceService _workspaceService;

    public WorkspaceReadinessService(IWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
    }

    public async Task<WorkspaceReadinessResponse> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        var workspaceDirectory = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory);

        var identityPath = Path.Combine(workspaceDirectory, KodaClawWorkspaceLayout.IdentityFile);
        var soulPath = Path.Combine(workspaceDirectory, KodaClawWorkspaceLayout.SoulFile);
        var userPath = Path.Combine(workspaceDirectory, KodaClawWorkspaceLayout.UserFile);

        var isIdentitySet = await IsFileCustomizedAsync(identityPath, DefaultWorkspaceTemplates.Identity(), cancellationToken);
        var isSoulSet = await IsFileCustomizedAsync(soulPath, DefaultWorkspaceTemplates.Soul(), cancellationToken);
        var isUserSet = await IsFileCustomizedAsync(userPath, DefaultWorkspaceTemplates.User(), cancellationToken);

        return new WorkspaceReadinessResponse(
            IsIdentitySet: isIdentitySet,
            IsSoulSet: isSoulSet,
            IsUserSet: isUserSet,
            HasAnyGap: !isIdentitySet || !isSoulSet || !isUserSet);
    }

    private static async Task<bool> IsFileCustomizedAsync(
        string path,
        string defaultContent,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return !string.Equals(content.Trim(), defaultContent.Trim(), StringComparison.Ordinal);
    }
}
