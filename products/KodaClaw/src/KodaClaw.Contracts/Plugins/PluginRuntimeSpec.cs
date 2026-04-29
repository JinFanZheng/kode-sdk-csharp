namespace KodaClaw.Contracts.Plugins;

public sealed record PluginRuntimeSpec(
    PluginTransportKind Transport,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? Url = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyDictionary<string, string>? EnvironmentReferences = null,
    IReadOnlyDictionary<string, string>? HeaderReferences = null);
