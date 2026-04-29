using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<ThemeMode>))]
public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2,
}
