namespace KodaClaw.Contracts.Sessions;

public sealed record ResumeSessionResponse(
    bool Ok,
    string ResumedSessionId);
