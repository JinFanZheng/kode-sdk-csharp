namespace KodaClaw.Contracts.Inbox;

public interface IInboxRepository
{
    Task UpsertAsync(InboxItem item, CancellationToken cancellationToken = default);

    Task<InboxItem?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InboxItem>> ListAsync(InboxQuery? query = null, CancellationToken cancellationToken = default);

    Task<bool> UpdateStatusAsync(
        string id,
        InboxItemStatus status,
        DateTimeOffset updatedAt,
        DateTimeOffset? resolvedAt = null,
        CancellationToken cancellationToken = default);
}
