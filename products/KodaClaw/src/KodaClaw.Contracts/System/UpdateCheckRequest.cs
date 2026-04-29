namespace KodaClaw.Contracts.System;

public sealed record UpdateCheckRequest(
    string? DesktopCurrentVersion = null,
    UpdateReleaseChannel? DesktopReleaseChannel = null);
