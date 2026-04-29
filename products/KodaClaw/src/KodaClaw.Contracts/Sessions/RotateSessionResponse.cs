namespace KodaClaw.Contracts.Sessions;

public sealed record RotateSessionResponse(
    bool Ok,
    string? PreviousSessionId);
