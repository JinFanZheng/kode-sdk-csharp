namespace KodaClaw.Contracts.Settings;

public interface ISettingsRepository
{
    Task<KodaClawSettings> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(KodaClawSettings settings, CancellationToken cancellationToken = default);
}
