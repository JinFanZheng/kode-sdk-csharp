namespace KodaClaw.Contracts.Plugins;

public sealed record PluginHealthcheckSpec(
    string? ToolName = null,
    int? IntervalSeconds = null,
    int? TimeoutSeconds = null);
