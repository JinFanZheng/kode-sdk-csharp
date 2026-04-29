using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Channels;

[JsonConverter(typeof(JsonStringEnumConverter<DeliveryMode>))]
public enum DeliveryMode
{
    AutoSend = 0,
    DraftApproval = 1,
    RequireApproval = 2,
}
