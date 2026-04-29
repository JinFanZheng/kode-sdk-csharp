namespace KodaClaw.Contracts.Channels;

public sealed record DeliveryRule(
    string Id,
    DeliveryMode Mode,
    DateTimeOffset UpdatedAt,
    bool AllowProactiveSend = false,
    bool MuteDuringQuietHours = true,
    int? MaxAutoRepliesPerHour = null);
