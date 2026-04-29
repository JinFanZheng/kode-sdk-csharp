using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Channels;

[JsonConverter(typeof(JsonStringEnumConverter<ChannelTurnOutcomeKind>))]
public enum ChannelTurnOutcomeKind
{
    NoAction = 0,
    DraftCreated = 1,
    ApprovalRequested = 2,
    Delivered = 3,
    Failed = 4,
}
