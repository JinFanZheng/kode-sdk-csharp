namespace KodaClaw.Contracts.Plugins;

public sealed record PluginSummary(
    string Id,
    string Name,
    string Version,
    IReadOnlyList<PluginType> Types,
    PluginInstallSource InstallSource,
    PluginTrustState TrustState,
    bool Enabled,
    PluginRuntimeState RuntimeState,
    string RootPath,
    DateTimeOffset UpdatedAt,
    string? LastError = null);
