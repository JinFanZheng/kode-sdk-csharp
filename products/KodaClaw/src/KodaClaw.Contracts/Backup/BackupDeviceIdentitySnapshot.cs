namespace KodaClaw.Contracts.Backup;

public sealed record BackupDeviceIdentitySnapshot(
    string? DeviceId,
    string? FingerprintHash,
    string? AppVersion,
    string? LastSeenAtUtc);
