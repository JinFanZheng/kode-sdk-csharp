namespace KodaClaw.Automation.Scheduler;

public interface IAutomationScheduler
{
    Task<int> TickAsync(CancellationToken cancellationToken = default);

    Task<int> RunOnceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Immediately starts execution of a specific automation definition, bypassing IsDue checks.
    /// Creates and persists a run record synchronously, then executes the agent in the background.
    /// Returns the RunId of the created run, or null if the definition was not found.
    /// </summary>
    Task<string?> TriggerDefinitionAsync(string definitionId, CancellationToken cancellationToken = default);
}
