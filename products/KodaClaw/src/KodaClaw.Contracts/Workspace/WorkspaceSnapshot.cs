namespace KodaClaw.Contracts.Workspace;

public sealed record WorkspaceSnapshot(
    string RootPath,
    int WorkspaceVersion,
    bool WorkspaceInitialized,
    bool RequiresBootstrap,
    string? ActiveMainSessionId,
    string? DeviceId,
    string? DeviceFingerprintHash = null,
    string? DeviceRotatedAtUtc = null,
    string? DeviceRotationReason = null,
    string? DeviceAppVersion = null,
    string? DeviceLastSeenAtUtc = null);
