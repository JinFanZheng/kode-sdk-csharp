namespace KodaClaw.Contracts.System;

public sealed record DeviceIdentity(
    string DeviceId,
    string MachineName,
    string Platform,
    string CreatedAtUtc,
    string? FingerprintHash = null,
    string? WorkspaceRootHash = null,
    string? AppVersion = null,
    string? LastSeenAtUtc = null,
    string? RotatedAtUtc = null,
    string? RotationReason = null);
