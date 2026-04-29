namespace KodaClaw.Contracts.Channels;

public interface IChannelAccountRepository
{
    Task UpsertAsync(ChannelAccount account, CancellationToken cancellationToken = default);

    Task<ChannelAccount?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChannelAccount>> ListAsync(
        ChannelAccountQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
