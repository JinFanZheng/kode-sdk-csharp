namespace KodaClaw.Contracts.Diagnostics;

public sealed record DiagnosticBundleRedactionSummary(
    bool IncludesRawSecrets,
    bool IncludesMessageBodies,
    IReadOnlyList<string> AppliedRules,
    IReadOnlyList<string> Notes);
