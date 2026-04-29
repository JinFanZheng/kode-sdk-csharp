namespace KodaClaw.Contracts.Channels;

public sealed record UpdateDeliveryRuleRequest(
    DeliveryMode Mode,
    bool AllowProactiveSend = false,
    bool MuteDuringQuietHours = true,
    int? MaxAutoRepliesPerHour = null);
