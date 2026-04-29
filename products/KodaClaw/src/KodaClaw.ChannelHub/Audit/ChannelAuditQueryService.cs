using KodaClaw.Contracts.Channels;

namespace KodaClaw.ChannelHub.Audit;

public sealed class ChannelAuditQueryService
{
    private readonly IChannelAuditRepository _auditRepository;

    public ChannelAuditQueryService(IChannelAuditRepository auditRepository)
    {
        _auditRepository = auditRepository ?? throw new ArgumentNullException(nameof(auditRepository));
    }

    public Task<IReadOnlyList<ChannelAuditEntry>> ListRecentByBindingIdAsync(
        string bindingId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return _auditRepository.ListByBindingIdAsync(bindingId, limit, cancellationToken);
    }
}
