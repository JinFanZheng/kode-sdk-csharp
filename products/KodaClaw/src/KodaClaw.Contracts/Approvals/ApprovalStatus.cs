using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Approvals;

[JsonConverter(typeof(JsonStringEnumConverter<ApprovalStatus>))]
public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Canceled = 3,
}
