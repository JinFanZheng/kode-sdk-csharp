using KodaClaw.Contracts.Repair;

namespace KodaClaw.Contracts.Backup;

public sealed record BackupImportPreflightResponse(
    DateTimeOffset EvaluatedAt,
    string WorkspaceRootPath,
    string ArchivePath,
    BackupManifest Manifest,
    bool ChecksumVerified,
    bool CanImport,
    RepairChecklist Checklist);
