using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Channels;

[JsonConverter(typeof(JsonStringEnumConverter<ChannelAccountState>))]
public enum ChannelAccountState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Degraded = 3,
}
