using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Inbox;

[JsonConverter(typeof(JsonStringEnumConverter<InboxItemStatus>))]
public enum InboxItemStatus
{
    Open = 0,
    Acknowledged = 1,
    Resolved = 2,
    Archived = 3,
}
