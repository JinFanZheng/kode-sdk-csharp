using KodaClaw.Contracts.System;

namespace KodaClaw.Contracts.Workspace;

public interface IWorkspaceService
{
    string RootPath { get; }

    Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<WorkspaceSnapshot> EnsureInitializedAsync(CancellationToken cancellationToken = default);

    Task<WorkspaceAppConfig> LoadAppConfigAsync(CancellationToken cancellationToken = default);

    Task SaveAppConfigAsync(WorkspaceAppConfig appConfig, CancellationToken cancellationToken = default);

    string GetSessionDirectory(string sessionId);

    IReadOnlyList<string> GetSkillsPaths();

    Task<WorkspaceMcpConfig> ReadMcpConfigAsync(CancellationToken cancellationToken = default);

    Task SaveMcpConfigAsync(WorkspaceMcpConfig config, CancellationToken cancellationToken = default);

    Task<GatewayConfig> ReadGatewayConfigAsync(CancellationToken cancellationToken = default);

    Task SaveGatewayConfigAsync(GatewayConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages tracked workspace files and commits with the given message.
    /// No-op (returns false) when git is not initialised or there is nothing to commit.
    /// Never throws.
    /// </summary>
    Task<bool> TryCommitWorkspaceAsync(string message, CancellationToken cancellationToken = default);
}
