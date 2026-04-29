namespace KodaClaw.Contracts.Diagnostics;

public sealed record DiagnosticBundleManifest(
    string Product,
    int FormatVersion,
    DateTimeOffset GeneratedAt,
    string ArchiveName,
    string SourceWorkspaceRoot,
    string? RequestedSessionId,
    DiagnosticBundleDesktopContext? DesktopContext,
    DiagnosticBundleRedactionSummary RedactionSummary,
    IReadOnlyList<DiagnosticBundleManifestEntry> Entries,
    IReadOnlyList<string> Includes,
    IReadOnlyList<string> Excludes,
    IReadOnlyList<string> Notes);
