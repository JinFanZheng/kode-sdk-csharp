using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Plugins;

[JsonConverter(typeof(JsonStringEnumConverter<PluginTransportKind>))]
public enum PluginTransportKind
{
    Stdio = 0,
    Http = 1,
    StreamableHttp = 2,
    Sse = 3,
}
