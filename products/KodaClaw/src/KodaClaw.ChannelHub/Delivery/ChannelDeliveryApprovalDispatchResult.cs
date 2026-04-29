using KodaClaw.Contracts.Approvals;

namespace KodaClaw.ChannelHub.Delivery;

public enum ChannelDeliveryApprovalDispatchStatus
{
    Completed = 0,
    NotFound = 1,
    NotPending = 2,
    InvalidApproval = 3,
    DeliveryFailed = 4,
}

public sealed record ChannelDeliveryApprovalDispatchResult(
    ChannelDeliveryApprovalDispatchStatus Status,
    Approval? Approval = null,
    string? Message = null);
