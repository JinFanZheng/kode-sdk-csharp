namespace KodaClaw.Contracts.Plugins;

public sealed record PluginDetail(
    PluginRecord Record,
    PluginPermissionRiskSummary PermissionSummary,
    PluginHealthSummary HealthSummary,
    IReadOnlyList<string> AvailableTools);
