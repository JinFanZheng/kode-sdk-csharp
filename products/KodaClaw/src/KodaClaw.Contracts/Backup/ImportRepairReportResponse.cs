using KodaClaw.Contracts.Repair;

namespace KodaClaw.Contracts.Backup;

public sealed record ImportRepairReportResponse(
    DateTimeOffset GeneratedAt,
    string WorkspaceRootPath,
    string ReportPath,
    RepairChecklist Checklist);
