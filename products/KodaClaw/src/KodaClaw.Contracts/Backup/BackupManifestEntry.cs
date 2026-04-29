namespace KodaClaw.Contracts.Backup;

public sealed record BackupManifestEntry(
    string Path,
    string Sha256,
    long SizeBytes,
    string Category);
