namespace KodaClaw.Contracts.Channels;

public sealed record ChannelsQueryResponse(
    IReadOnlyList<ChannelThreadSummary> Items);
