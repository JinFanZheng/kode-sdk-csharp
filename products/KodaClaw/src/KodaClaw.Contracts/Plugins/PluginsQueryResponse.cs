namespace KodaClaw.Contracts.Plugins;

public sealed record PluginsQueryResponse(
    IReadOnlyList<PluginSummary> Items);
