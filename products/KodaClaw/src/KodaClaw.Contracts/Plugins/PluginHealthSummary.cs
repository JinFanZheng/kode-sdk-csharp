namespace KodaClaw.Contracts.Plugins;

public sealed record PluginHealthSummary(
    string Status,
    string? Message,
    DateTimeOffset? LastHealthAt,
    int RestartCount)
{
    public bool IsHealthy => string.Equals(Status, "Healthy", StringComparison.OrdinalIgnoreCase);
}
