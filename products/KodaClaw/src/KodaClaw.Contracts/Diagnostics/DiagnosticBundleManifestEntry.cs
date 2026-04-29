namespace KodaClaw.Contracts.Diagnostics;

public sealed record DiagnosticBundleManifestEntry(
    string Path,
    string Sha256,
    long SizeBytes,
    string Category);
