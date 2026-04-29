namespace KodaClaw.Contracts.Diagnostics;

public interface IDiagnosticsService
{
    void Record(DiagnosticEvent diagnosticEvent);

    IReadOnlyList<DiagnosticEvent> GetRecent(int limit = 50, string? correlationId = null);

    IReadOnlyList<DiagnosticEvent> Query(DiagnosticsQuery? query = null);

    DiagnosticsStatsResponse GetStats(DateTimeOffset? since = null);

    Task ClearAsync(DateTimeOffset? before = null, CancellationToken cancellationToken = default);

    IAsyncEnumerable<DiagnosticEvent> SubscribeAsync(CancellationToken cancellationToken);
}
