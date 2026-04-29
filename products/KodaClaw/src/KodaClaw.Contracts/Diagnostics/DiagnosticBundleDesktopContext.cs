using KodaClaw.Contracts.System;

namespace KodaClaw.Contracts.Diagnostics;

public sealed record DiagnosticBundleDesktopContext(
    bool DesktopMode,
    string Platform,
    string AppVersion,
    UpdateReleaseChannel ReleaseChannel,
    string? GatewayLifecycleMode = null);
