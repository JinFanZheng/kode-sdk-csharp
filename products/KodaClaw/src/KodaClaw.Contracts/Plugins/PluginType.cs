using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Plugins;

[JsonConverter(typeof(JsonStringEnumConverter<PluginType>))]
public enum PluginType
{
    Tool = 0,
    Channel = 1,
    Memory = 2,
    Ui = 3,
}
