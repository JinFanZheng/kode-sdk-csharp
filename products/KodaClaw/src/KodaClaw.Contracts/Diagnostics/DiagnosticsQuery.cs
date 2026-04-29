namespace KodaClaw.Contracts.Diagnostics;

public sealed record DiagnosticsQuery(
    int Limit = 50,
    string? CorrelationId = null,
    string? SessionId = null,
    string? Source = null,
    string? EventType = null,
    string[]? Levels = null,
    DateTimeOffset? DateFrom = null,
    DateTimeOffset? DateTo = null)
{
    public string? Level => Levels?.FirstOrDefault();
}
