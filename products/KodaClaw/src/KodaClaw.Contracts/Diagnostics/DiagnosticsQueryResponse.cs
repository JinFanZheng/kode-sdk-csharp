namespace KodaClaw.Contracts.Diagnostics;

public sealed record DiagnosticsQueryResponse(
    IReadOnlyList<DiagnosticEvent> Events);
