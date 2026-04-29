namespace KodaClaw.Contracts.Diagnostics;

public sealed record DiagnosticEvent(
    string Id,
    string Source,
    string EventType,
    string Level,
    string Message,
    DateTimeOffset Timestamp,
    string? CorrelationId = null,
    string? SessionId = null,
    IReadOnlyDictionary<string, string?>? Attributes = null);
