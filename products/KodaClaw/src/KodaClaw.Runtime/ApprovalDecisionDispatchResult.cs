using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;

namespace KodaClaw.Runtime;

public enum ApprovalDecisionDispatchStatus
{
    Completed = 0,
    NotFound = 1,
    NotPending = 2,
    LiveSessionRequired = 3,
    LiveApprovalMissing = 4,
    DecisionTimedOut = 5,
    RuntimeUnavailable = 6,
}

public sealed record ApprovalDecisionDispatchResult(
    ApprovalDecisionDispatchStatus Status,
    Approval? Approval = null,
    string? Message = null);
