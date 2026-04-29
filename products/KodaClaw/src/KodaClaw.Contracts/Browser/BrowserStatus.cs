namespace KodaClaw.Contracts.Browser;

public sealed record BrowserStatus(
    bool IsConnected,
    IReadOnlyList<BrowserDevice> Devices,
    DateTimeOffset? LastHeartbeat = null);
