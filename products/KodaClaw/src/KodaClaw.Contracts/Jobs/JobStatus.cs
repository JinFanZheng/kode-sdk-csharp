namespace KodaClaw.Contracts.Jobs;

public enum JobStatus
{
    Pending = 0,
    Cancelled = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Paused = 5,
}
