using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Approvals;

[JsonConverter(typeof(JsonStringEnumConverter<ApprovalKind>))]
public enum ApprovalKind
{
    OutboundMessage = 0,
    OutboundEmail = 1,
    PluginAuthorization = 2,
    ChannelDelivery = 3,
    AutomationAction = 4,
    ExternalAction = 5,
}
