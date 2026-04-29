namespace KodaClaw.Contracts.Diagnostics;

public sealed record DiagnosticBundleExportRequest(
    string? SessionId = null,
    int TimelineLimit = 120,
    string? ArchivePath = null,
    DiagnosticBundleDesktopContext? DesktopContext = null);
