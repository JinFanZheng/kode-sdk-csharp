using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Automations;

[JsonConverter(typeof(JsonStringEnumConverter<AutomationDefinitionSource>))]
public enum AutomationDefinitionSource
{
    Heartbeat,
    Manual,
    OneShot,
}
