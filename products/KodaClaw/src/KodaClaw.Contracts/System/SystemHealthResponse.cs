using KodaClaw.Contracts.Settings;

namespace KodaClaw.Contracts.System;

public sealed record SystemHealthResponse(
    string Name,
    string Status,
    AppMode Mode);
