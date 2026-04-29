using KodaClaw.Contracts.Settings;

namespace KodaClaw.Contracts.Bootstrap;

public sealed record BootstrapStateResponse(
    string WorkspaceRootPath,
    int WorkspaceVersion,
    bool WorkspaceInitialized,
    bool RequiresBootstrap,
    string? ActiveMainSessionId,
    AppMode Mode);
