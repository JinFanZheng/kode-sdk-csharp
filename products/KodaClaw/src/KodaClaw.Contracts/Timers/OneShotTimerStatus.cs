using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Timers;

[JsonConverter(typeof(JsonStringEnumConverter<OneShotTimerStatus>))]
public enum OneShotTimerStatus
{
    Pending,
    Fired,
    Cancelled,
}
