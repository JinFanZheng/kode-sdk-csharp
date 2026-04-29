namespace KodaClaw.Contracts.Plugins;

public sealed record PluginQuery(
    PluginType? Type = null,
    PluginTrustState? TrustState = null,
    bool? Enabled = null,
    PluginRuntimeState? RuntimeState = null,
    int Limit = 50,
    int Offset = 0);
