using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Repair;

[JsonConverter(typeof(JsonStringEnumConverter<RepairChecklistState>))]
public enum RepairChecklistState
{
    Pending,
    Completed,
}
