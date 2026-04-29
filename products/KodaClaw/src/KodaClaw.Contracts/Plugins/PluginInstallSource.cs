using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Plugins;

[JsonConverter(typeof(JsonStringEnumConverter<PluginInstallSource>))]
public enum PluginInstallSource
{
    Bundled = 0,
    LocalDirectory = 1,
}
