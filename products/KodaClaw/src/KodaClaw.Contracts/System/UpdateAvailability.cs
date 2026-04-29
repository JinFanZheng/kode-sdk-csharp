using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.System;

[JsonConverter(typeof(JsonStringEnumConverter<UpdateAvailability>))]
public enum UpdateAvailability
{
    Unknown = 0,
    UpToDate = 1,
    UpdateAvailable = 2,
    CheckFailed = 3,
}
