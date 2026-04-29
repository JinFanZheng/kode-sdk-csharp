namespace KodaClaw.Contracts.Plugins;

public sealed record PluginRecord(
    string Id,
    PluginManifest Manifest,
    PluginInstallSource InstallSource,
    string RootPath,
    PluginTrustState TrustState,
    bool Enabled,
    PluginRuntimeState RuntimeState,
    DateTimeOffset DiscoveredAt,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastStartedAt = null,
    DateTimeOffset? LastStoppedAt = null,
    DateTimeOffset? LastHealthAt = null,
    int RestartCount = 0,
    string? LastError = null,
    PluginTrustEvidence? TrustEvidence = null);
