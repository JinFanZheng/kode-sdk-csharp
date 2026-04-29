namespace KodaClaw.Contracts.Channels;

public sealed record ChannelSendReceipt(string? ExternalMessageId, DateTimeOffset SentAt);
