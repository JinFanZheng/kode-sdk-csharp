namespace KodaClaw.Contracts.Browser;

public sealed record BrowserDevice(
    string DeviceId,
    string Label,
    DateTimeOffset PairedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastSeenAt = null,
    bool IsOnline = false);
