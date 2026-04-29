namespace KodaClaw.Contracts.Timers;

public interface IOneShotTimerRepository
{
    Task<OneShotTimerRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OneShotTimerRecord>> ListPendingAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task AddAsync(OneShotTimerRecord record, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(OneShotTimerRecord record, CancellationToken cancellationToken = default);
}
