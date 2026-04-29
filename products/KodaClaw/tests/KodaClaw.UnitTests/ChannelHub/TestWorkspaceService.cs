using KodaClaw.Contracts;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;

namespace KodaClaw.UnitTests.ChannelHub;

internal sealed class TestWorkspaceService : IWorkspaceService
{
    public TestWorkspaceService(string rootPath)
    {
        RootPath = rootPath;
    }

    public string RootPath { get; }

    public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CreateSnapshot(initialized: true));
    }

    public Task<WorkspaceSnapshot> EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(Path.Combine(RootPath, KodaClawWorkspaceLayout.ConfigDirectory));
        return Task.FromResult(CreateSnapshot(initialized: true));
    }

    public Task<WorkspaceAppConfig> LoadAppConfigAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new WorkspaceAppConfig());
    }

    public Task SaveAppConfigAsync(WorkspaceAppConfig appConfig, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public string GetSessionDirectory(string sessionId)
    {
        return Path.Combine(RootPath, KodaClawWorkspaceLayout.SessionsDirectory, sessionId);
    }

    public IReadOnlyList<string> GetSkillsPaths()
    {
        return
        [
            Path.Combine(AppContext.BaseDirectory, "skills"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills"),
            Path.Combine(RootPath, KodaClawWorkspaceLayout.WorkspaceSkillsDirectory),
        ];
    }

    public Task<WorkspaceMcpConfig> ReadMcpConfigAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new WorkspaceMcpConfig());

    public Task SaveMcpConfigAsync(WorkspaceMcpConfig config, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<GatewayConfig> ReadGatewayConfigAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new GatewayConfig());

    public Task SaveGatewayConfigAsync(GatewayConfig config, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

        public Task<bool> TryCommitWorkspaceAsync(string message, CancellationToken cancellationToken = default) => Task.FromResult(false);


    private WorkspaceSnapshot CreateSnapshot(bool initialized)
    {
        return new WorkspaceSnapshot(
            RootPath: RootPath,
            WorkspaceVersion: KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
            WorkspaceInitialized: initialized,
            RequiresBootstrap: !initialized,
            ActiveMainSessionId: null,
            DeviceId: null);
    }
}
