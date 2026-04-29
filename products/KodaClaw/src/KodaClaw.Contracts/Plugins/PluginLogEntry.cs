namespace KodaClaw.Contracts.Plugins;

public sealed record PluginLogEntry(
    string EntryId,
    string PluginId,
    string Level,
    string Source,
    string Message,
    DateTimeOffset Timestamp,
    string? PayloadJson = null);
