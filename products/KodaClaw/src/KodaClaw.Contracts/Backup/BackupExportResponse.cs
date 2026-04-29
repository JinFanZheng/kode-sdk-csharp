namespace KodaClaw.Contracts.Backup;

public sealed record BackupExportResponse(
    DateTimeOffset GeneratedAt,
    string WorkspaceRootPath,
    string ArchivePath,
    BackupManifest Manifest);
