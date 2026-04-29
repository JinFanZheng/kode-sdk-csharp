using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Inbox;

[JsonConverter(typeof(JsonStringEnumConverter<InboxItemKind>))]
public enum InboxItemKind
{
    Approval = 0,
    AutomationResult = 1,
    PluginRequest = 2,
    ChannelUpdate = 3,
    Alert = 4,
    TaskResult = 5,
    Information = 6,
}
