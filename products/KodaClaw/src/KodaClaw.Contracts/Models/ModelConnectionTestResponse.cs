namespace KodaClaw.Contracts.Models;

public sealed record ModelConnectionTestResponse(
    bool Ok,
    int LatencyMs,
    string? ModelId,
    string? Error,
    string? ErrorMessage = null
);
