using KodaClaw.Contracts.Repair;

namespace KodaClaw.Contracts.Backup;

public sealed record BackupImportResponse(
    DateTimeOffset ImportedAt,
    string WorkspaceRootPath,
    string ArchivePath,
    string RepairReportPath,
    BackupManifest Manifest,
    RepairChecklist Checklist,
    IReadOnlyList<string> RestoredPaths,
    IReadOnlyList<string> SkippedPaths);
