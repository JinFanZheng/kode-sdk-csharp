namespace KodaClaw.Contracts.Plugins;

public sealed record PluginDisplaySpec(
    string? Description = null,
    string? Icon = null,
    string? AccentColor = null);
