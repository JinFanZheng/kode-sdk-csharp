namespace KodaClaw.Contracts.Automations;

public interface IAutomationDefinitionRepository
{
    Task UpsertAsync(AutomationDefinition definition, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomationDefinition>> ListAsync(
        AutomationDefinitionQuery? query = null,
        CancellationToken cancellationToken = default);

    Task<AutomationDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
