using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Plugins;

[JsonConverter(typeof(JsonStringEnumConverter<PluginTrustState>))]
public enum PluginTrustState
{
    Untrusted = 0,
    Trusted = 1,
    Signed = 2,
}
