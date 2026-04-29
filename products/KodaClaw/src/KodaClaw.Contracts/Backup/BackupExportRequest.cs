namespace KodaClaw.Contracts.Backup;

public sealed record BackupExportRequest(
    string? ArchivePath = null);
