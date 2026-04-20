namespace KodaClaw.Contracts;

public sealed record ChannelSendReceipt(string? ExternalMessageId, DateTimeOffset SentAt);
