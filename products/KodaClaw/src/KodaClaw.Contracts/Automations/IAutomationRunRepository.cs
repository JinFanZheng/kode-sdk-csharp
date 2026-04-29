namespace KodaClaw.Contracts.Automations;

public interface IAutomationRunRepository
{
    Task AddAsync(AutomationRunRecord runRecord, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(AutomationRunRecord runRecord, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomationRunRecord>> ListAsync(
        AutomationRunQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<AutomationRunRecord?> GetByIdAsync(string runId, CancellationToken cancellationToken = default);
}
