using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Channels;

[JsonConverter(typeof(JsonStringEnumConverter<ChannelEventType>))]
public enum ChannelEventType
{
    MessageReceived = 0,
    MessageEdited = 1,
    MessageDeleted = 2,
    ReactionReceived = 3,
    AccountConnected = 4,
    AccountDisconnected = 5,
    DeliveryFailed = 6,
}
