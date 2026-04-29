namespace KodaClaw.Contracts.Channels;

public sealed record PatchChannelAccountRequest(
    string? DisplayName = null,
    DeliveryMode? DeliveryMode = null,
    bool? Enabled = null,
    string? ConfigurationJson = null);
