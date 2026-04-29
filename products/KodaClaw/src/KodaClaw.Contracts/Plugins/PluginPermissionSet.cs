namespace KodaClaw.Contracts.Plugins;

public sealed record PluginPermissionSet(
    IReadOnlyList<string>? Filesystem = null,
    bool Network = false,
    bool Notifications = false,
    bool Background = false,
    IReadOnlyList<string>? Channels = null,
    IReadOnlyList<string>? UiPanels = null,
    IReadOnlyList<string>? Secrets = null);
