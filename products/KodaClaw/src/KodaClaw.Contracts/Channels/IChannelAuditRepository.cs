namespace KodaClaw.Contracts.Channels;

public interface IChannelAuditRepository
{
    Task AppendAsync(ChannelAuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChannelAuditEntry>> ListByBindingIdAsync(
        string bindingId,
        int limit = 50,
        CancellationToken cancellationToken = default);
}
