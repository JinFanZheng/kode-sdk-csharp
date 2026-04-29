using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.System;

[JsonConverter(typeof(JsonStringEnumConverter<UpdateReleaseChannel>))]
public enum UpdateReleaseChannel
{
    Stable = 0,
    Preview = 1,
    Nightly = 2,
    Custom = 3,
}
