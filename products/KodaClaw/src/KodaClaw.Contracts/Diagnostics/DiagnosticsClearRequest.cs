namespace KodaClaw.Contracts.Diagnostics;

public sealed record DiagnosticsClearRequest(DateTimeOffset? Before = null);
