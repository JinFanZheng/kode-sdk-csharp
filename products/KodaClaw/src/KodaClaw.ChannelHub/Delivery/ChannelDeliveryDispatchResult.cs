using KodaClaw.Contracts.Channels;

namespace KodaClaw.ChannelHub.Delivery;

public sealed record ChannelDeliveryDispatchResult(
    bool Succeeded,
    ChannelTurnOutcome Outcome,
    string? ErrorMessage = null);
