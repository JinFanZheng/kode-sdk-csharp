namespace KodaClaw.Automation.Scheduler;

public sealed class AutomationSchedulerOptions
{
    public bool Enabled { get; set; } = false;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan FailureRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Runs in Queued or Running state for longer than this threshold are considered stale
    /// (e.g., left over from a previous crash) and will be marked Failed by the recovery pass.
    /// Default: 10 minutes — long enough that a freshly-created manual trigger run is never
    /// incorrectly treated as stale.
    /// </summary>
    public TimeSpan StaleRunThreshold { get; set; } = TimeSpan.FromMinutes(10);
}
