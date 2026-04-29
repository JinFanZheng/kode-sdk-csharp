namespace KodaClaw.Contracts.Sessions;

public sealed record SessionsQueryResponse(
    IReadOnlyList<SessionSummary> Sessions);
