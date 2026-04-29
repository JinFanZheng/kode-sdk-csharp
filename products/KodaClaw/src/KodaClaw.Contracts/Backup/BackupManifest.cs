namespace KodaClaw.Contracts.Backup;

public sealed record BackupManifest(
    string Product,
    int FormatVersion,
    int WorkspaceVersion,
    DateTimeOffset GeneratedAt,
    string ArchiveName,
    string SourceWorkspaceRoot,
    BackupDeviceIdentitySnapshot? SourceDevice,
    IReadOnlyList<BackupManifestEntry> Entries,
    IReadOnlyList<string> Includes,
    IReadOnlyList<string> Excludes);
