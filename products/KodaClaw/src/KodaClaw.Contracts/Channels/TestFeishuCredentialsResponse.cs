namespace KodaClaw.Contracts.Channels;

public sealed record TestFeishuCredentialsResponse(
    bool Ok,
    string? AppName = null,
    string? Error = null);
