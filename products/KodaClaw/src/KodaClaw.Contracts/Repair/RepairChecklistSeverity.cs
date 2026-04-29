using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Repair;

[JsonConverter(typeof(JsonStringEnumConverter<RepairChecklistSeverity>))]
public enum RepairChecklistSeverity
{
    Info,
    Warning,
    ActionRequired,
    Blocking,
}
