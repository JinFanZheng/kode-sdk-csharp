namespace KodaClaw.Contracts.Approvals;

public interface IApprovalRepository
{
    Task UpsertAsync(Approval approval, CancellationToken cancellationToken = default);

    Task<Approval?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Approval>> ListAsync(ApprovalQuery? query = null, CancellationToken cancellationToken = default);

    Task<bool> TransitionAsync(
        string id,
        ApprovalStatus status,
        DateTimeOffset updatedAt,
        string? decidedBy = null,
        string? decisionNote = null,
        CancellationToken cancellationToken = default);
}
